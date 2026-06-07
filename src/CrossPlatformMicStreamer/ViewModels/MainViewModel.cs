using System.Collections.ObjectModel;
using Avalonia.Threading;
using CrossPlatformMicStreamer.Audio;
using CrossPlatformMicStreamer.Network;

namespace CrossPlatformMicStreamer.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly DiscoveryService _discovery;
    private readonly AudioStreamConnection _audioConnection = new();
    private readonly MicrophoneCapture _capture = new();
    private readonly AudioPlayback _playback = new();
    private readonly Dictionary<string, PeerInfo> _peers = new();
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly LocalIdentity _identity;
    private readonly AdaptiveLatencySettings _latencySettings = new();
    private UiPreferences _loadedPreferences = new();
    private bool _applyingPreferences;
    private bool _uiPreferencesApplied;
    private bool _suppressSessionRefresh;

    private SessionMode _sessionMode = SessionMode.Receive;
    private bool _streamingMicForPeer;
    private AudioDeviceInfo? _selectedInputDevice;
    private AudioDeviceInfo? _selectedOutputDevice;
    private PeerInfo? _selectedPeer;
    private string? _selectedPeerId;
    private Task? _sendLoopTask;
    private bool _audioPathsRunning;
    private bool _adaptiveSessionActive;
    private DateTime _lastAudioRestartUtc = DateTime.MinValue;
    private int _stableIntervalCount;
    private int _badIntervalCount;
    private ConnectionPhase _connectionPhase = ConnectionPhase.Idle;
    private string? _errorMessage;
    private double _inputMeterLevel;
    private double _outputMeterLevel;
    private double _networkMs;
    private int _bufferMs = AdaptiveLatencySettings.DefaultLatencyMs;
    private int _manualBufferMs = AdaptiveLatencySettings.DefaultLatencyMs;
    private bool _isBufferLocked;
    private bool _isEditingBuffer;
    private string _bufferEditText = AdaptiveLatencySettings.DefaultLatencyMs.ToString();
    private AudioBitDepth _selectedBitDepth = AudioBitDepth.Bits32;
    private bool _showBufferTestStats;
    private int _lastEvalUnderruns;
    private int _lastEvalSaturated;
    private int _lastEvalDrops;
    private bool _hasLastEval;
    private int _bufferStatUnderruns;
    private int _bufferStatSaturated;
    private int _bufferStatDrops;
    private int _bufferStatBadWindows;
    private int _bufferStatStableWindows;
    private bool _bufferStatUnderrunsOver;
    private bool _bufferStatSaturatedOver;
    private bool _bufferStatDropsOver;
    private bool _bufferStatIsLive;

    private bool LocalSendActive =>
        _sessionMode == SessionMode.Send &&
        _selectedPeer != null &&
        _selectedInputDevice != null;

    private bool LocalReceiveActive =>
        _sessionMode == SessionMode.Receive &&
        _selectedPeer != null &&
        _selectedOutputDevice != null;

    private bool WantsSession =>
        _streamingMicForPeer || LocalSendActive || LocalReceiveActive;

    public ObservableCollection<AudioDeviceInfo> InputDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = new();
    public ObservableCollection<PeerInfo> Peers { get; } = new();

    public IReadOnlyList<AudioBitDepth> BitDepthOptions { get; } =
    [
        AudioBitDepth.Bits16,
        AudioBitDepth.Bits24,
        AudioBitDepth.Bits32,
    ];

    public AudioBitDepth SelectedBitDepth
    {
        get => _selectedBitDepth;
        set
        {
            if (!SetProperty(ref _selectedBitDepth, value))
            {
                return;
            }

            _latencySettings.BitDepth = value;
            SavePreferences();
            _ = RefreshSessionAsync();
        }
    }

    public SessionMode SessionMode
    {
        get => _sessionMode;
        set
        {
            if (!SetProperty(ref _sessionMode, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(IsSendMode));
            RaisePropertyChanged(nameof(IsReceiveMode));
            UpdateDiscoveryScanning();
            SavePreferences();
            _ = RefreshSessionAsync();
        }
    }

    public bool IsSendMode
    {
        get => _sessionMode == SessionMode.Send;
        set
        {
            if (value)
            {
                SessionMode = SessionMode.Send;
            }
        }
    }

    public bool IsReceiveMode
    {
        get => _sessionMode == SessionMode.Receive;
        set
        {
            if (value)
            {
                SessionMode = SessionMode.Receive;
            }
        }
    }

    public bool HasSelectedPeer => _selectedPeer != null;

    public AudioDeviceInfo? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            if (!SetProperty(ref _selectedInputDevice, value))
            {
                return;
            }

            SavePreferences();
            _ = RefreshSessionAsync();
        }
    }

    public AudioDeviceInfo? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value))
            {
                return;
            }

            SavePreferences();
            _ = RefreshSessionAsync();
        }
    }

    public PeerInfo? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (value == null &&
                _selectedPeer != null &&
                ShouldKeepSavedPeerSelection())
            {
                SessionLog.Write("Ignoring spurious peer deselect.");
                ResyncSelectedPeerBinding();
                return;
            }

            if (!SetProperty(ref _selectedPeer, value))
            {
                return;
            }

            _selectedPeerId = value?.Id;
            RaisePropertyChanged(nameof(HasSelectedPeer));
            UpdateDiscoveryScanning();

            if (value != null)
            {
                RememberPeerInPreferences(value);
            }

            if (_applyingPreferences || _suppressSessionRefresh)
            {
                return;
            }

            SavePreferences();
            _ = RefreshSessionAsync();
        }
    }

    public string? SelectedPeerId => _selectedPeerId;

    public ConnectionPhase ConnectionPhase
    {
        get => _connectionPhase;
        private set
        {
            if (SetProperty(ref _connectionPhase, value))
            {
                UpdateFooterText();
            }
        }
    }

    public string FooterText { get; private set; } = "Idle";

    public int BufferMs
    {
        get => _bufferMs;
        private set => SetProperty(ref _bufferMs, value);
    }

    public bool IsBufferLocked
    {
        get => _isBufferLocked;
        set
        {
            if (!SetProperty(ref _isBufferLocked, value))
            {
                return;
            }

            if (value)
            {
                _manualBufferMs = SnapBufferMs(
                    _audioPathsRunning ? _latencySettings.TargetLatencyMs : BufferMs);
                _bufferEditText = _manualBufferMs.ToString();
                RaisePropertyChanged(nameof(BufferEditText));
            }
            else
            {
                _stableIntervalCount = 0;
                _badIntervalCount = 0;
            }

            SavePreferences();
            UpdateBufferTestStats();
        }
    }

    public bool ShowBufferTestStats
    {
        get => _showBufferTestStats;
        private set => SetProperty(ref _showBufferTestStats, value);
    }

    public int BufferStatUnderruns
    {
        get => _bufferStatUnderruns;
        private set => SetProperty(ref _bufferStatUnderruns, value);
    }

    public int BufferStatSaturated
    {
        get => _bufferStatSaturated;
        private set => SetProperty(ref _bufferStatSaturated, value);
    }

    public int BufferStatDrops
    {
        get => _bufferStatDrops;
        private set => SetProperty(ref _bufferStatDrops, value);
    }

    public int BufferStatBadWindows
    {
        get => _bufferStatBadWindows;
        private set => SetProperty(ref _bufferStatBadWindows, value);
    }

    public int BufferStatStableWindows
    {
        get => _bufferStatStableWindows;
        private set => SetProperty(ref _bufferStatStableWindows, value);
    }

    public int BufferStatUnderrunLimit => _latencySettings.MinUnderrunsToIncrease;

    public int BufferStatSaturatedLimit => _latencySettings.MinSaturatedToIncrease;

    public int BufferStatDropLimit => _latencySettings.MinUnderrunsToIncrease;

    public int BufferStatBadLimit => _latencySettings.BadIntervalsBeforeIncrease;

    public int BufferStatStableLimit => _latencySettings.StableIntervalsBeforeDecrease;

    public bool BufferStatUnderrunsOver
    {
        get => _bufferStatUnderrunsOver;
        private set => SetProperty(ref _bufferStatUnderrunsOver, value);
    }

    public bool BufferStatSaturatedOver
    {
        get => _bufferStatSaturatedOver;
        private set => SetProperty(ref _bufferStatSaturatedOver, value);
    }

    public bool BufferStatDropsOver
    {
        get => _bufferStatDropsOver;
        private set => SetProperty(ref _bufferStatDropsOver, value);
    }

    public bool BufferStatIsLive
    {
        get => _bufferStatIsLive;
        private set => SetProperty(ref _bufferStatIsLive, value);
    }

    public bool IsEditingBuffer
    {
        get => _isEditingBuffer;
        private set => SetProperty(ref _isEditingBuffer, value);
    }

    public string BufferEditText
    {
        get => _bufferEditText;
        set => SetProperty(ref _bufferEditText, value);
    }

    public double InputMeterLevel
    {
        get => _inputMeterLevel;
        private set => SetProperty(ref _inputMeterLevel, value);
    }

    public double OutputMeterLevel
    {
        get => _outputMeterLevel;
        private set => SetProperty(ref _outputMeterLevel, value);
    }

    public MainViewModel()
    {
        PortAudioBootstrap.EnsureInitialized();

        _identity = LocalIdentityFactory.Create();
        _discovery = new DiscoveryService(_identity);
        _discovery.PeerDiscovered += OnPeerDiscovered;

        _audioConnection.Connected += OnConnected;
        _audioConnection.ConnectionLost += OnConnectionLost;
        _audioConnection.RemoteInputDeviceRequested += OnRemoteInputDeviceRequested;
        _audioConnection.RemoteLatencyChanged += OnRemoteLatencyChanged;
        _audioConnection.RemoteBitDepthChanged += OnRemoteBitDepthChanged;
        _audioConnection.AudioReceived += chunk =>
        {
            if (LocalReceiveActive)
            {
                _playback.Enqueue(chunk);
            }
        };

        _latencySettings.BitDepth = _selectedBitDepth;

        _loadedPreferences = UiPreferencesStore.Load();
        LoadDevices();
        _audioConnection.StartListening();
        _discovery.Start();

        UpdateFooterText();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SavePreferences();
        _ = PeerCleanupLoopAsync(_sessionCts.Token);
        _ = ConnectionMaintenanceLoopAsync(_sessionCts.Token);
        _ = MetricsUpdateLoopAsync(_sessionCts.Token);
        _ = AdaptiveLatencyLoopAsync(_sessionCts.Token);
        _ = AutoConnectLoopAsync(_sessionCts.Token);
    }

    public void CompleteUiInitialization()
    {
        if (_uiPreferencesApplied)
        {
            ResyncUiBindings();
            ScheduleAutoConnect();
            return;
        }

        ApplyLoadedPreferences();
        _uiPreferencesApplied = true;
        ResyncUiBindings();
        SavePreferences();
        ScheduleAutoConnect();
    }

    private void ScheduleAutoConnect()
    {
        if (!_loadedPreferences.AutoConnect || !WantsSession)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (!_loadedPreferences.AutoConnect || !WantsSession || _selectedPeer == null)
            {
                return;
            }

            SessionLog.Write($"Auto-connecting to {_selectedPeer.DisplayName}...");
            _ = RefreshSessionAsync();
        }, DispatcherPriority.Loaded);
    }

    public void AddManualPeer(string addressInput)
    {
        var address = NetworkEndpoints.NormalizeAddressString(addressInput.Trim());
        if (!System.Net.IPAddress.TryParse(address, out _))
        {
            ConnectionPhase = ConnectionPhase.Error;
            _errorMessage = "Invalid IP";
            UpdateFooterText();
            return;
        }

        _discovery.RegisterPeerAddress(address);

        var peer = new PeerInfo(
            $"manual:{address}",
            address,
            address,
            NetworkConstants.AudioPort,
            DateTime.UtcNow,
            IsManual: true);

        SelectedPeer = UpsertPeer(peer);
    }

    public void RefreshDiscovery()
    {
        _discovery.ProbeNow();
    }

    private void UpdateBufferTestStats()
    {
        if (!_audioPathsRunning || !LocalReceiveActive || IsBufferLocked)
        {
            if (ShowBufferTestStats)
            {
                ShowBufferTestStats = false;
            }

            return;
        }

        ShowBufferTestStats = true;

        var inGracePeriod = DateTime.UtcNow - _lastAudioRestartUtc <
            TimeSpan.FromSeconds(_latencySettings.GracePeriodAfterRestartSeconds);

        int underruns;
        int saturated;
        int drops;

        if (inGracePeriod)
        {
            underruns = _hasLastEval ? _lastEvalUnderruns : 0;
            saturated = _hasLastEval ? _lastEvalSaturated : 0;
            drops = _hasLastEval ? _lastEvalDrops : 0;
            BufferStatIsLive = false;
        }
        else
        {
            underruns = _playback.PeekUnderrunCount();
            saturated = _playback.PeekSaturatedTicks();
            drops = _capture.PeekDroppedChunkCount();
            BufferStatIsLive = true;
        }

        BufferStatUnderruns = underruns;
        BufferStatSaturated = saturated;
        BufferStatDrops = drops;
        BufferStatBadWindows = _badIntervalCount;
        BufferStatStableWindows = _stableIntervalCount;
        BufferStatUnderrunsOver = underruns >= _latencySettings.MinUnderrunsToIncrease;
        BufferStatSaturatedOver = saturated >= _latencySettings.MinSaturatedToIncrease;
        BufferStatDropsOver = drops >= _latencySettings.MinUnderrunsToIncrease;
    }

    private void RecordBufferEvaluation(int underruns, int saturated, int drops)
    {
        _lastEvalUnderruns = underruns;
        _lastEvalSaturated = saturated;
        _lastEvalDrops = drops;
        _hasLastEval = true;
    }

    private async Task MetricsUpdateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var inputLevel = LocalSendActive || _streamingMicForPeer ? _capture.MeterLevel : 0f;
            var outputLevel = LocalReceiveActive ? _playback.MeterLevel : 0f;
            var networkMs = _audioConnection.IsConnected ? _audioConnection.NetworkMs : 0d;
            var bufferMs = _audioPathsRunning ? _latencySettings.TargetLatencyMs : _manualBufferMs;

            Dispatcher.UIThread.Post(() =>
            {
                InputMeterLevel = inputLevel;
                OutputMeterLevel = outputLevel;

                if (Math.Abs(_networkMs - networkMs) > 0.5 || BufferMs != bufferMs)
                {
                    _networkMs = networkMs;
                    BufferMs = bufferMs;
                    if (!IsBufferLocked && _manualBufferMs != bufferMs)
                    {
                        _manualBufferMs = bufferMs;
                        _bufferEditText = bufferMs.ToString();
                        RaisePropertyChanged(nameof(BufferEditText));
                    }

                    UpdateFooterText();
                }

                UpdateBufferTestStats();
            });

            await Task.Delay(50, cancellationToken);
        }
    }

    private async Task AdaptiveLatencyLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(_latencySettings.EvaluationIntervalMs, cancellationToken);

            if (!_audioPathsRunning)
            {
                continue;
            }

            if (!LocalReceiveActive)
            {
                continue;
            }

            if (IsBufferLocked)
            {
                continue;
            }

            if (DateTime.UtcNow - _lastAudioRestartUtc <
                TimeSpan.FromSeconds(_latencySettings.GracePeriodAfterRestartSeconds))
            {
                continue;
            }

            var underruns = _playback.ConsumeUnderrunCount();
            var saturated = _playback.ConsumeSaturatedTicks();
            var drops = _capture.ConsumeDroppedChunkCount();
            var needsMoreBuffer = _latencySettings.NeedsMoreBuffer(underruns, saturated, drops);

            RecordBufferEvaluation(underruns, saturated, drops);

            if (needsMoreBuffer)
            {
                _stableIntervalCount = 0;

                if (++_badIntervalCount >= _latencySettings.BadIntervalsBeforeIncrease)
                {
                    _badIntervalCount = 0;

                    if (_latencySettings.TryIncrease())
                    {
                        _manualBufferMs = _latencySettings.TargetLatencyMs;
                        await ApplyBufferChangeAsync();
                    }
                }
            }
            else
            {
                _badIntervalCount = 0;

                if (!_latencySettings.IsAtMin &&
                    ++_stableIntervalCount >= _latencySettings.StableIntervalsBeforeDecrease)
                {
                    _stableIntervalCount = 0;

                    if (_latencySettings.TryDecrease())
                    {
                        _manualBufferMs = _latencySettings.TargetLatencyMs;
                        await ApplyBufferChangeAsync();
                    }
                }
            }

            var evalSummary =
                $"eval u {underruns} s {saturated} d {drops} -> " +
                (needsMoreBuffer ? "bad" : "ok");
            Dispatcher.UIThread.Post(UpdateBufferTestStats);
            SessionLog.Write(evalSummary);
        }
    }

    public void BeginBufferEdit()
    {
        BufferEditText = BufferMs.ToString();
        IsEditingBuffer = true;
    }

    public void ApplyBufferEdit()
    {
        if (!int.TryParse(BufferEditText.Trim(), out var parsed))
        {
            CancelBufferEdit();
            return;
        }

        ApplyManualBufferMs(SnapBufferMs(parsed));
        IsEditingBuffer = false;
    }

    public void CancelBufferEdit()
    {
        IsEditingBuffer = false;
        BufferEditText = BufferMs.ToString();
    }

    public void ToggleBufferEdit()
    {
        if (IsEditingBuffer)
        {
            ApplyBufferEdit();
            return;
        }

        BeginBufferEdit();
    }

    private static int SnapBufferMs(int ms) =>
        Math.Clamp(
            (int)Math.Round(ms / (double)AdaptiveLatencySettings.StepMs) * AdaptiveLatencySettings.StepMs,
            AdaptiveLatencySettings.MinLatencyMs,
            AdaptiveLatencySettings.MaxLatencyMs);

    private void ApplyManualBufferMs(int bufferMs)
    {
        _manualBufferMs = bufferMs;
        _bufferEditText = bufferMs.ToString();
        RaisePropertyChanged(nameof(BufferEditText));

        if (_latencySettings.TargetLatencyMs == bufferMs)
        {
            BufferMs = bufferMs;
            UpdateFooterText();
            SavePreferences();
            return;
        }

        _latencySettings.SetTargetLatencyMs(bufferMs);
        BufferMs = bufferMs;
        _stableIntervalCount = 0;
        _badIntervalCount = 0;
        _audioConnection.UpdateFrameDuration(_latencySettings.FrameDurationMs);
        UpdateFooterText();
        SavePreferences();
        _ = ApplyBufferChangeAsync();
    }

    private async Task ApplyBufferChangeAsync()
    {
        _audioConnection.UpdateFrameDuration(_latencySettings.FrameDurationMs);

        if (_audioPathsRunning && WantsSession && _audioConnection.IsConnected)
        {
            await RestartAudioPathsAsync();
        }

        await SendLatencySyncAsync();
        SavePreferences();
    }

    private void OnConnectionLost(string? address)
    {
        ConnectionPhase = ConnectionPhase.Reconnecting;

        if (_selectedPeer != null && WantsSession)
        {
            _ = ReconnectToSelectedPeerAsync();
        }
        else if (_streamingMicForPeer)
        {
            _streamingMicForPeer = false;
            StopAudioStreamsOnly();
        }

        UpdateDiscoveryScanning();
    }

    private async Task RestartAudioPathsAsync()
    {
        await _refreshLock.WaitAsync(_sessionCts.Token);

        try
        {
            if (!_audioConnection.IsConnected || !WantsSession)
            {
                return;
            }

            StopAudioStreamsOnly();
            StartAudioPaths();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void StopAudioStreamsOnly()
    {
        _capture.Stop();
        _playback.Stop();
        _sendLoopTask = null;
        _audioPathsRunning = false;
    }

    private void LoadDevices()
    {
        InputDevices.Clear();
        foreach (var device in PortAudioDeviceEnumerator.GetInputDevices())
        {
            InputDevices.Add(device);
        }

        OutputDevices.Clear();
        foreach (var device in PortAudioDeviceEnumerator.GetOutputDevices())
        {
            OutputDevices.Add(device);
        }

        _selectedInputDevice = ResolveDeviceInList(
            InputDevices,
            PortAudioDeviceEnumerator.GetDefaultInputDevice());
        _selectedOutputDevice = ResolveDeviceInList(
            OutputDevices,
            PortAudioDeviceEnumerator.GetDefaultOutputDevice());
    }

    private void ApplyLoadedPreferences()
    {
        _applyingPreferences = true;

        try
        {
            if (Enum.TryParse(_loadedPreferences.SessionMode, out SessionMode mode))
            {
                _sessionMode = mode;
            }

            _selectedBitDepth = AudioBitDepthExtensions.ParseWireValue(_loadedPreferences.BitDepth);
            _latencySettings.BitDepth = _selectedBitDepth;

            _manualBufferMs = SnapBufferMs(_loadedPreferences.ManualBufferMs);
            BufferMs = _manualBufferMs;
            _bufferEditText = _manualBufferMs.ToString();
            _isBufferLocked = _loadedPreferences.IsBufferLocked;

            var savedInput = FindSavedDevice(
                InputDevices,
                _loadedPreferences.InputDeviceIndex,
                _loadedPreferences.InputDeviceName);
            if (savedInput != null)
            {
                _selectedInputDevice = savedInput;
            }

            var savedOutput = FindSavedDevice(
                OutputDevices,
                _loadedPreferences.OutputDeviceIndex,
                _loadedPreferences.OutputDeviceName);
            if (savedOutput != null)
            {
                _selectedOutputDevice = savedOutput;
            }

            RestoreSavedPeer(_loadedPreferences);
            TrySelectSavedPeer(_loadedPreferences);
        }
        finally
        {
            _applyingPreferences = false;
        }

        SessionLog.Write(
            $"Applied preferences: mode={_sessionMode}, " +
            $"input={_selectedInputDevice?.Name ?? "none"}, " +
            $"output={_selectedOutputDevice?.Name ?? "none"}, " +
            $"peer={_selectedPeer?.DisplayName ?? "none"}, " +
            $"bitDepth={_selectedBitDepth}, buffer={_manualBufferMs} ms");

        UpdateDiscoveryScanning();
    }

    private void ResyncUiBindings()
    {
        _suppressSessionRefresh = true;

        try
        {
            _selectedInputDevice = RebindDeviceSelection(InputDevices, _selectedInputDevice);
            _selectedOutputDevice = RebindDeviceSelection(OutputDevices, _selectedOutputDevice);

            if (_selectedPeer != null)
            {
                ResyncSelectedPeerBinding();
            }
        }
        finally
        {
            _suppressSessionRefresh = false;
        }

        RaisePropertyChanged(nameof(SessionMode));
        RaisePropertyChanged(nameof(IsSendMode));
        RaisePropertyChanged(nameof(IsReceiveMode));
        RaisePropertyChanged(nameof(SelectedBitDepth));
        RaisePropertyChanged(nameof(BufferEditText));
        RaisePropertyChanged(nameof(BufferMs));
        RaisePropertyChanged(nameof(IsBufferLocked));
        RaisePropertyChanged(nameof(SelectedInputDevice));
        RaisePropertyChanged(nameof(SelectedOutputDevice));
        RaisePropertyChanged(nameof(SelectedPeer));
        RaisePropertyChanged(nameof(HasSelectedPeer));
    }

    private static AudioDeviceInfo? RebindDeviceSelection(
        IEnumerable<AudioDeviceInfo> devices,
        AudioDeviceInfo? current)
    {
        var deviceList = devices as IList<AudioDeviceInfo> ?? devices.ToList();
        if (deviceList.Count == 0)
        {
            return null;
        }

        if (current == null)
        {
            return deviceList[0];
        }

        return deviceList.FirstOrDefault(device => device.Index == current.Index && device.Name == current.Name)
               ?? deviceList.FirstOrDefault(device => device.Index == current.Index)
               ?? deviceList[0];
    }

    private void RestoreSavedPeer(UiPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(preferences.PeerAddress) &&
            string.IsNullOrWhiteSpace(preferences.PeerId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferences.PeerAddress))
        {
            var address = NetworkEndpoints.NormalizeAddressString(preferences.PeerAddress);
            if (!System.Net.IPAddress.TryParse(address, out _))
            {
                return;
            }

            RegisterSavedPeerAddress(preferences);

            var peerId = string.IsNullOrWhiteSpace(preferences.PeerId)
                ? $"manual:{address}"
                : preferences.PeerId;
            var peerName = string.IsNullOrWhiteSpace(preferences.PeerName)
                ? address
                : preferences.PeerName;

            var stubPeer = new PeerInfo(
                peerId,
                peerName,
                address,
                NetworkConstants.AudioPort,
                DateTime.UtcNow,
                IsManual: true);

            SetSelectedPeerSilently(UpsertPeer(stubPeer));
            return;
        }

        TrySelectSavedPeer(preferences);
    }

    private void SavePreferences()
    {
        if (_applyingPreferences)
        {
            return;
        }

        var peerId = _selectedPeerId ?? _loadedPreferences.PeerId;
        var peerAddress = _selectedPeer == null
            ? _loadedPreferences.PeerAddress
            : NetworkEndpoints.NormalizeAddressString(_selectedPeer.Address);
        var peerName = _selectedPeer?.Name ?? _loadedPreferences.PeerName;

        _loadedPreferences = new UiPreferences
        {
            SessionMode = _sessionMode.ToString(),
            PeerId = peerId,
            PeerAddress = peerAddress,
            PeerName = peerName,
            InputDeviceIndex = _selectedInputDevice?.Index,
            InputDeviceName = _selectedInputDevice?.Name,
            OutputDeviceIndex = _selectedOutputDevice?.Index,
            OutputDeviceName = _selectedOutputDevice?.Name,
            BitDepth = _selectedBitDepth.ToWireValue(),
            ManualBufferMs = _manualBufferMs,
            IsBufferLocked = _isBufferLocked,
            AutoConnect = true,
        };

        UiPreferencesStore.Save(_loadedPreferences);
    }

    private bool ShouldKeepSavedPeerSelection() =>
        _loadedPreferences.AutoConnect &&
        (!string.IsNullOrWhiteSpace(_loadedPreferences.PeerAddress) ||
         !string.IsNullOrWhiteSpace(_loadedPreferences.PeerId));

    private void RememberPeerInPreferences(PeerInfo peer)
    {
        _loadedPreferences.PeerId = peer.Id;
        _loadedPreferences.PeerAddress = NetworkEndpoints.NormalizeAddressString(peer.Address);
        _loadedPreferences.PeerName = peer.Name;
    }

    private void ResyncSelectedPeerBinding()
    {
        if (_selectedPeer == null)
        {
            return;
        }

        var listedPeer = Peers.FirstOrDefault(peer => peer.Id == _selectedPeer.Id);
        if (listedPeer == null &&
            !string.IsNullOrWhiteSpace(_loadedPreferences.PeerAddress))
        {
            RestoreSavedPeer(_loadedPreferences);
            listedPeer = _selectedPeer;
        }

        if (listedPeer == null)
        {
            return;
        }

        _suppressSessionRefresh = true;
        try
        {
            _selectedPeer = listedPeer;
            _selectedPeerId = listedPeer.Id;
        }
        finally
        {
            _suppressSessionRefresh = false;
        }

        RaisePropertyChanged(nameof(SelectedPeer));
        RaisePropertyChanged(nameof(HasSelectedPeer));
    }

    private static AudioDeviceInfo? FindSavedDevice(
        IEnumerable<AudioDeviceInfo> devices,
        int? index,
        string? name)
    {
        if (index.HasValue)
        {
            var byIndex = devices.FirstOrDefault(device => device.Index == index.Value);
            if (byIndex != null)
            {
                return byIndex;
            }
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            var exact = devices.FirstOrDefault(device => device.Name == name);
            if (exact != null)
            {
                return exact;
            }

            return devices.FirstOrDefault(device =>
                device.Name.StartsWith(name, StringComparison.Ordinal) ||
                name.StartsWith(device.Name, StringComparison.Ordinal));
        }

        return null;
    }

    private static AudioDeviceInfo? ResolveDeviceInList(
        IEnumerable<AudioDeviceInfo> devices,
        AudioDeviceInfo? candidate)
    {
        var deviceList = devices as IList<AudioDeviceInfo> ?? devices.ToList();
        if (deviceList.Count == 0)
        {
            return null;
        }

        if (candidate == null)
        {
            return deviceList[0];
        }

        return deviceList.FirstOrDefault(device =>
                   device.Index == candidate.Index &&
                   device.Name == candidate.Name)
               ?? deviceList.FirstOrDefault(device => device.Index == candidate.Index)
               ?? deviceList[0];
    }

    private void RegisterSavedPeerAddress(UiPreferences preferences)
    {
        if (string.IsNullOrWhiteSpace(preferences.PeerAddress))
        {
            return;
        }

        var address = NetworkEndpoints.NormalizeAddressString(preferences.PeerAddress);
        if (!System.Net.IPAddress.TryParse(address, out _))
        {
            return;
        }

        _discovery.RegisterPeerAddress(address);
    }

    private bool TrySelectSavedPeer(UiPreferences preferences)
    {
        if (!string.IsNullOrEmpty(preferences.PeerId))
        {
            if (_peers.TryGetValue(preferences.PeerId, out var peerById))
            {
                SetSelectedPeerSilently(peerById);
                return true;
            }

            var listedPeer = Peers.FirstOrDefault(peer => peer.Id == preferences.PeerId);
            if (listedPeer != null)
            {
                SetSelectedPeerSilently(listedPeer);
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(preferences.PeerAddress))
        {
            return _selectedPeer != null;
        }

        var normalized = NetworkEndpoints.NormalizeAddressString(preferences.PeerAddress);
        var peerByAddress = _peers.Values.FirstOrDefault(peer =>
            string.Equals(peer.Address, normalized, StringComparison.Ordinal) ||
            peer.ConnectAddresses.Any(address =>
                string.Equals(NetworkEndpoints.NormalizeAddressString(address), normalized, StringComparison.Ordinal)));

        if (peerByAddress != null)
        {
            SetSelectedPeerSilently(peerByAddress);
            return true;
        }

        return _selectedPeer != null;
    }

    private void SetSelectedPeerSilently(PeerInfo peer)
    {
        var listedPeer = Peers.FirstOrDefault(candidate => candidate.Id == peer.Id) ?? peer;
        _selectedPeer = listedPeer;
        _selectedPeerId = listedPeer.Id;
        RememberPeerInPreferences(listedPeer);
        RaisePropertyChanged(nameof(SelectedPeer));
        RaisePropertyChanged(nameof(HasSelectedPeer));
        UpdateDiscoveryScanning();
    }

    private async Task AutoConnectLoopAsync(CancellationToken cancellationToken)
    {
        if (!_loadedPreferences.AutoConnect)
        {
            return;
        }

        for (var attempt = 0; attempt < 60 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

            var shouldRefresh = Dispatcher.UIThread.Invoke(() =>
            {
                if (!_uiPreferencesApplied)
                {
                    return false;
                }

                if (_selectedPeer == null)
                {
                    RegisterSavedPeerAddress(_loadedPreferences);
                    TrySelectSavedPeer(_loadedPreferences);
                }

                return WantsSession &&
                       (!_audioPathsRunning ||
                        (_selectedPeer != null && !_audioConnection.IsConnectedToPeer(_selectedPeer)));
            });

            if (shouldRefresh)
            {
                SessionLog.Write(
                    $"Auto-connect attempt {attempt + 1}: peer={_selectedPeer?.DisplayName ?? "none"}, " +
                    $"connected={_audioConnection.IsConnectedToPeer(_selectedPeer!)}");
                await RefreshSessionAsync();
            }

            if (_audioPathsRunning &&
                _selectedPeer != null &&
                _audioConnection.IsConnectedToPeer(_selectedPeer))
            {
                return;
            }
        }
    }

    private void OnPeerDiscovered(PeerInfo peer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpsertPeer(peer);

            if (_applyingPreferences ||
                !_loadedPreferences.AutoConnect ||
                _selectedPeer != null)
            {
                return;
            }

            var matchesId = !string.IsNullOrEmpty(_loadedPreferences.PeerId) &&
                            peer.Id == _loadedPreferences.PeerId;
            var matchesAddress = PeerMatchesSavedAddress(peer, _loadedPreferences.PeerAddress);

            if (!matchesId && !matchesAddress)
            {
                return;
            }

            SetSelectedPeerSilently(peer);
            SavePreferences();

            if (WantsSession)
            {
                ScheduleAutoConnect();
            }
        });
    }

    private static bool PeerMatchesSavedAddress(PeerInfo peer, string? savedAddress)
    {
        if (string.IsNullOrWhiteSpace(savedAddress))
        {
            return false;
        }

        var normalized = NetworkEndpoints.NormalizeAddressString(savedAddress);
        return string.Equals(peer.Address, normalized, StringComparison.Ordinal) ||
               peer.ConnectAddresses.Any(address =>
                   string.Equals(NetworkEndpoints.NormalizeAddressString(address), normalized, StringComparison.Ordinal));
    }

    private void OnRemoteInputDeviceRequested(int deviceIndex)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var device = InputDevices.FirstOrDefault(candidate => candidate.Index == deviceIndex)
                ?? InputDevices.FirstOrDefault();
            if (device == null)
            {
                return;
            }

            _selectedInputDevice = device;
            RaisePropertyChanged(nameof(SelectedInputDevice));
            _streamingMicForPeer = true;
            _ = RefreshSessionAsync();
        });
    }

    private void OnRemoteLatencyChanged(int latencyMs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsBufferLocked)
            {
                return;
            }

            if (LocalReceiveActive)
            {
                return;
            }

            if (_latencySettings.TargetLatencyMs == latencyMs)
            {
                return;
            }

            _latencySettings.SetTargetLatencyMs(latencyMs);
            _manualBufferMs = latencyMs;
            BufferMs = latencyMs;
            _bufferEditText = latencyMs.ToString();
            RaisePropertyChanged(nameof(BufferEditText));
            _audioConnection.UpdateFrameDuration(_latencySettings.FrameDurationMs);
            UpdateFooterText();
            SavePreferences();
            _ = RestartAudioPathsAsync();
        });
    }

    private void OnRemoteBitDepthChanged(int bitDepthValue)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (LocalSendActive)
            {
                return;
            }

            var bitDepth = AudioBitDepthExtensions.ParseWireValue(bitDepthValue);
            if (_selectedBitDepth == bitDepth)
            {
                return;
            }

            _selectedBitDepth = bitDepth;
            _latencySettings.BitDepth = bitDepth;
            RaisePropertyChanged(nameof(SelectedBitDepth));
            _ = RestartAudioPathsAsync();
        });
    }

    private async Task SendLatencySyncAsync()
    {
        if (_selectedPeer == null || !_audioConnection.IsConnected)
        {
            return;
        }

        try
        {
            await _audioConnection.SendSetLatencyAsync(
                _latencySettings.TargetLatencyMs,
                _sessionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendBitDepthSyncAsync()
    {
        if (_selectedPeer == null || !_audioConnection.IsConnected)
        {
            return;
        }

        try
        {
            await _audioConnection.SendSetBitDepthAsync(
                _selectedBitDepth.ToWireValue(),
                _sessionCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private PeerInfo UpsertPeer(PeerInfo peer)
    {
        var canonicalId = ResolveCanonicalPeerId(peer);
        var existing = _peers.TryGetValue(canonicalId, out var byId)
            ? byId
            : _peers.Values.FirstOrDefault(candidate => PeerMerge.SharesAddress(candidate, peer));

        if (existing != null)
        {
            canonicalId = PeerMerge.ChooseCanonicalId(existing, peer);
            peer = PeerMerge.Merge(existing, peer with { Id = canonicalId });
        }
        else
        {
            peer = peer with { Id = canonicalId };
        }

        var duplicateIds = _peers.Values
            .Where(existingPeer => existingPeer.Id != canonicalId && PeerMerge.SharesAddress(existingPeer, peer))
            .Select(existingPeer => existingPeer.Id)
            .ToList();

        if (_selectedPeerId != null &&
            (duplicateIds.Contains(_selectedPeerId) ||
             _selectedPeerId == canonicalId ||
             (_selectedPeer != null && PeerMerge.SharesAddress(_selectedPeer, peer))))
        {
            _selectedPeerId = canonicalId;
        }

        peer = peer with { LastSeenUtc = DateTime.UtcNow };

        var connectedAddress = _audioConnection.ConnectedAddress;
        if (!string.IsNullOrWhiteSpace(connectedAddress) &&
            _selectedPeerId == canonicalId &&
            peer.ConnectAddresses.All(address =>
                !string.Equals(
                    NetworkEndpoints.NormalizeAddressString(address),
                    connectedAddress,
                    StringComparison.Ordinal)))
        {
            var addresses = peer.AllAddresses?.ToList() ?? [];
            addresses.Add(connectedAddress);
            peer = peer with
            {
                Address = NetworkEndpoints.SelectBestPeerAddress(connectedAddress, addresses),
                AllAddresses = addresses
                    .Select(NetworkEndpoints.NormalizeAddressString)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        _peers[canonicalId] = peer;

        var existingIndex = -1;
        for (var i = 0; i < Peers.Count; i++)
        {
            if (Peers[i].Id == canonicalId)
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            Peers[existingIndex] = peer;
        }
        else
        {
            Peers.Add(peer);
        }

        foreach (var duplicateId in duplicateIds)
        {
            _peers.Remove(duplicateId);

            for (var i = Peers.Count - 1; i >= 0; i--)
            {
                if (Peers[i].Id == duplicateId)
                {
                    Peers.RemoveAt(i);
                }
            }
        }

        if (_selectedPeerId == canonicalId)
        {
            var listedPeer = Peers.FirstOrDefault(candidate => candidate.Id == canonicalId) ?? peer;
            _selectedPeer = listedPeer;
            RememberPeerInPreferences(listedPeer);
            RaisePropertyChanged(nameof(SelectedPeer));
        }

        if (duplicateIds.Count > 0 && _selectedPeerId == canonicalId)
        {
            ResyncSelectedPeerBinding();
        }

        return peer;
    }

    private string ResolveCanonicalPeerId(PeerInfo peer)
    {
        foreach (var existing in _peers.Values)
        {
            if (existing.Id == peer.Id)
            {
                return peer.Id;
            }

            if (PeerMerge.SharesAddress(existing, peer))
            {
                return PeerMerge.ChooseCanonicalId(existing, peer);
            }
        }

        return peer.Id;
    }

    private void OnConnected(string remoteAddress)
    {
        if (_selectedPeer == null && !WantsSession)
        {
            return;
        }

        var normalizedRemote = NetworkEndpoints.NormalizeAddressString(remoteAddress);

        if (_selectedPeer != null)
        {
            var selectedAddresses = _selectedPeer.ConnectAddresses
                .Select(NetworkEndpoints.NormalizeAddressString)
                .ToHashSet(StringComparer.Ordinal);

            if (!selectedAddresses.Contains(normalizedRemote))
            {
                if (_sessionMode != SessionMode.Receive)
                {
                    _audioConnection.Disconnect();
                    return;
                }

                var addresses = _selectedPeer.AllAddresses?.ToList() ?? [];
                if (!addresses.Contains(normalizedRemote, StringComparer.Ordinal))
                {
                    addresses.Add(normalizedRemote);
                }

                SetSelectedPeerSilently(UpsertPeer(_selectedPeer with
                {
                    Address = NetworkEndpoints.SelectBestPeerAddress(normalizedRemote, addresses),
                    AllAddresses = addresses
                        .Select(NetworkEndpoints.NormalizeAddressString)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                }));
            }
        }

        if (_audioPathsRunning &&
            _selectedPeer != null &&
            _audioConnection.IsConnectedToPeer(_selectedPeer))
        {
            UpdateDiscoveryScanning();
            return;
        }

        _ = RefreshSessionAsync();
        UpdateDiscoveryScanning();
    }

    private void UpdateDiscoveryScanning()
    {
        var pauseScanning = WantsSession;
        _discovery.SetAggressiveScanningEnabled(!pauseScanning);
    }

    private async Task<bool> EnsureConnectedToPeerAsync()
    {
        if (_selectedPeer == null)
        {
            return false;
        }

        if (_audioConnection.IsConnectedToPeer(_selectedPeer))
        {
            return true;
        }

        if (!ConnectionPolicy.ShouldInitiateConnection(_identity.Id, _selectedPeer.Id))
        {
            return _audioConnection.IsConnectedToPeer(_selectedPeer);
        }

        return await _audioConnection.TryConnectToPeerAsync(_selectedPeer, _sessionCts.Token);
    }

    private async Task ConnectionMaintenanceLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(2);

            try
            {
                if (WantsSession)
                {
                    if (_audioConnection.IsConnectedToPeer(_selectedPeer!))
                    {
                        if (!_audioPathsRunning)
                        {
                            await RefreshSessionAsync();
                        }
                    }
                    else if (_audioConnection.IsConnected)
                    {
                        if (!_audioPathsRunning)
                        {
                            await RefreshSessionAsync();
                        }
                    }
                    else if (ConnectionPolicy.ShouldInitiateConnection(_identity.Id, _selectedPeer!.Id))
                    {
                        delay = TimeSpan.FromMilliseconds(750);
                        await ReconnectToSelectedPeerAsync(showInitialStatus: false);
                    }
                    else if (!_audioPathsRunning && _audioConnection.IsConnected)
                    {
                        await RefreshSessionAsync();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                ConnectionPhase = ConnectionPhase.Error;
                _errorMessage = "Connection lost";
                UpdateFooterText();
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task ReconnectToSelectedPeerAsync(bool showInitialStatus = true)
    {
        if (_selectedPeer == null || !WantsSession)
        {
            return;
        }

        await _reconnectLock.WaitAsync(_sessionCts.Token);

        try
        {
            if (_audioConnection.IsConnectedToPeer(_selectedPeer))
            {
                if (!_audioPathsRunning)
                {
                    await RefreshSessionAsync();
                }

                return;
            }

            if (!ConnectionPolicy.ShouldInitiateConnection(_identity.Id, _selectedPeer.Id))
            {
                ConnectionPhase = ConnectionPhase.Connecting;
                UpdateFooterText();
                return;
            }

            StopAudioStreamsOnly();

            if (showInitialStatus)
            {
                ConnectionPhase = ConnectionPhase.Reconnecting;
                UpdateFooterText();
            }

            SessionLog.Write($"Reconnecting to {_selectedPeer.DisplayName}...");

            for (var attempt = 0; attempt < 8 && !_sessionCts.IsCancellationRequested; attempt++)
            {
                if (await _audioConnection.TryConnectToPeerAsync(_selectedPeer, _sessionCts.Token))
                {
                    await RefreshSessionAsync();
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(400 + attempt * 200), _sessionCts.Token);
            }

            ConnectionPhase = ConnectionPhase.Reconnecting;
            UpdateFooterText();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private async Task PeerCleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            if (_selectedPeer != null && WantsSession)
            {
                continue;
            }

            var peersToCheck = _peers.Values.Where(peer => !peer.IsManual).ToList();
            if (peersToCheck.Count == 0)
            {
                continue;
            }

            var staleIds = new List<string>();

            foreach (var peer in peersToCheck)
            {
                if (_selectedPeer != null && peer.Id == _selectedPeer.Id)
                {
                    continue;
                }

                var alive = await PeerReachability.IsAudioPortOpenAsync(
                    peer.ConnectAddresses,
                    peer.TcpPort,
                    cancellationToken);

                if (!alive)
                {
                    staleIds.Add(peer.Id);
                }
            }

            if (staleIds.Count == 0)
            {
                continue;
            }

            Dispatcher.UIThread.Post(() =>
            {
                foreach (var id in staleIds)
                {
                    _peers.Remove(id);

                    for (var i = Peers.Count - 1; i >= 0; i--)
                    {
                        if (Peers[i].Id == id)
                        {
                            if (_selectedPeer?.Id == id)
                            {
                                var replacement = Peers.FirstOrDefault(candidate =>
                                    candidate.Id != id &&
                                    PeerMerge.SharesAddress(candidate, _selectedPeer));

                                if (replacement != null)
                                {
                                    SetSelectedPeerSilently(replacement);
                                }
                                else if (!WantsSession)
                                {
                                    SelectedPeer = null;
                                }
                            }

                            Peers.RemoveAt(i);
                        }
                    }
                }
            });
        }
    }

    private async Task RefreshSessionAsync()
    {
        await _refreshLock.WaitAsync(_sessionCts.Token);

        try
        {
            StopAudioStreamsOnly();

            if (_selectedPeer == null || !WantsSession)
            {
                if (_selectedPeer == null &&
                    ShouldKeepSavedPeerSelection())
                {
                    RestoreSavedPeer(_loadedPreferences);
                }

                if (_selectedPeer == null || !WantsSession)
                {
                    _streamingMicForPeer = false;
                    _audioConnection.Disconnect();
                    ConnectionPhase = ConnectionPhase.Idle;
                    _errorMessage = null;
                    UpdateDiscoveryScanning();
                    UpdateFooterText();
                    return;
                }
            }

            if (!_audioConnection.IsConnectedToPeer(_selectedPeer))
            {
                ConnectionPhase = ConnectionPhase.Connecting;
                _errorMessage = null;
                UpdateDiscoveryScanning();
                UpdateFooterText();

                if (!await EnsureConnectedToPeerAsync() ||
                    !_audioConnection.IsConnectedToPeer(_selectedPeer))
                {
                    return;
                }
            }

            StartAudioPaths();
            ConnectionPhase = ConnectionPhase.Connected;
            _errorMessage = null;
            UpdateDiscoveryScanning();
            UpdateFooterText();
            SavePreferences();
        }
        catch (Exception ex)
        {
            ConnectionPhase = ConnectionPhase.Error;
            _errorMessage = ex.Message;
            UpdateDiscoveryScanning();
            UpdateFooterText();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void StartAudioPaths()
    {
        if (!_adaptiveSessionActive)
        {
            _latencySettings.SetTargetLatencyMs(SnapBufferMs(_manualBufferMs));
            BufferMs = _latencySettings.TargetLatencyMs;
            _bufferEditText = _manualBufferMs.ToString();
            RaisePropertyChanged(nameof(BufferEditText));
            _stableIntervalCount = 0;
            _badIntervalCount = 0;
            _adaptiveSessionActive = true;
        }

        _audioConnection.UpdateFrameDuration(_latencySettings.FrameDurationMs);

        try
        {
            if (LocalReceiveActive)
            {
                _playback.Start(_selectedOutputDevice!.Index, _latencySettings);
            }

            var captureActive = _streamingMicForPeer || LocalSendActive;
            if (captureActive && _selectedInputDevice != null)
            {
                _capture.Start(_selectedInputDevice.Index, _latencySettings);
                _sendLoopTask = Task.Run(SendLoopAsync);
            }

            _audioPathsRunning = LocalReceiveActive || (captureActive && _selectedInputDevice != null);
            _lastAudioRestartUtc = DateTime.UtcNow;
            _playback.ConsumeUnderrunCount();
            _playback.ConsumeSaturatedTicks();
            _capture.ConsumeDroppedChunkCount();

            if (LocalReceiveActive)
            {
                _ = SendLatencySyncAsync();
            }

            if (LocalSendActive || _streamingMicForPeer)
            {
                _ = SendBitDepthSyncAsync();
            }
        }
        catch (Exception ex)
        {
            StopAudioPaths();
            ConnectionPhase = ConnectionPhase.Error;
            _errorMessage = ex.Message;
            UpdateFooterText();
        }
    }

    private async Task SendLoopAsync()
    {
        while ((_streamingMicForPeer || LocalSendActive) && !_sessionCts.IsCancellationRequested)
        {
            if (_capture.TryDequeue(out var chunk) && chunk != null)
            {
                _audioConnection.EnqueueOutgoing(chunk);
            }
            else
            {
                await Task.Delay(1, _sessionCts.Token);
            }
        }
    }

    private void StopAudioPaths()
    {
        _capture.Stop();
        _playback.Stop();
        _sendLoopTask = null;
        _audioPathsRunning = false;
        _adaptiveSessionActive = false;
        _stableIntervalCount = 0;
        _badIntervalCount = 0;
    }

    private void UpdateFooterText()
    {
        var peerName = _selectedPeer?.Name ?? _selectedPeer?.Address;
        FooterText = _connectionPhase switch
        {
            ConnectionPhase.Error => string.IsNullOrWhiteSpace(peerName)
                ? _errorMessage ?? "Error"
                : $"{peerName} · {_errorMessage ?? "error"}",
            ConnectionPhase.Connected when !string.IsNullOrWhiteSpace(peerName) =>
                $"{peerName} · {_sessionMode.ToString().ToLowerInvariant()} · {_networkMs:F0} ms",
            ConnectionPhase.Connecting when !string.IsNullOrWhiteSpace(peerName) =>
                $"{peerName} · connecting",
            ConnectionPhase.Reconnecting when !string.IsNullOrWhiteSpace(peerName) =>
                $"{peerName} · reconnecting",
            ConnectionPhase.Idle when !string.IsNullOrWhiteSpace(peerName) =>
                $"{peerName} · idle",
            _ => "Idle",
        };

        RaisePropertyChanged(nameof(FooterText));
    }

    public void Dispose()
    {
        SavePreferences();
        _sessionCts.Cancel();
        StopAudioPaths();
        _capture.Dispose();
        _playback.Dispose();
        _audioConnection.Dispose();
        _discovery.Dispose();
        PortAudioBootstrap.Terminate();
        _sessionCts.Dispose();
        _refreshLock.Dispose();
        _reconnectLock.Dispose();
    }
}
