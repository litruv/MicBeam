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

            _ = RefreshSessionAsync();
        }
    }

    public PeerInfo? SelectedPeer
    {
        get => _selectedPeer;
        set
        {
            if (!SetProperty(ref _selectedPeer, value))
            {
                return;
            }

            _selectedPeerId = value?.Id;
            RaisePropertyChanged(nameof(HasSelectedPeer));
            UpdateDiscoveryScanning();
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

            if (!value)
            {
                _stableIntervalCount = 0;
                _badIntervalCount = 0;
            }
        }
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

        LoadDevices();
        _audioConnection.StartListening();
        _discovery.Start();

        UpdateFooterText();
        _ = PeerCleanupLoopAsync(_sessionCts.Token);
        _ = ConnectionMaintenanceLoopAsync(_sessionCts.Token);
        _ = MetricsUpdateLoopAsync(_sessionCts.Token);
        _ = AdaptiveLatencyLoopAsync(_sessionCts.Token);
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
                    if (!IsBufferLocked)
                    {
                        _manualBufferMs = bufferMs;
                    }

                    UpdateFooterText();
                }
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

        if (_latencySettings.TargetLatencyMs == bufferMs)
        {
            BufferMs = bufferMs;
            UpdateFooterText();
            return;
        }

        _latencySettings.SetTargetLatencyMs(bufferMs);
        BufferMs = bufferMs;
        _stableIntervalCount = 0;
        _badIntervalCount = 0;
        _audioConnection.UpdateFrameDuration(_latencySettings.FrameDurationMs);
        UpdateFooterText();
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

        SelectedInputDevice = PortAudioDeviceEnumerator.GetDefaultInputDevice();
        SelectedOutputDevice = PortAudioDeviceEnumerator.GetDefaultOutputDevice();
    }

    private void OnPeerDiscovered(PeerInfo peer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpsertPeer(peer);
        });
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
            _audioConnection.UpdateFrameDuration(_latencySettings.FrameDurationMs);
            UpdateFooterText();
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

        RemoveDuplicatePeers(canonicalId, peer);
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

        if (_selectedPeerId != null &&
            _selectedPeerId != canonicalId &&
            (_selectedPeer == null || PeerMerge.SharesAddress(_selectedPeer, peer)))
        {
            _selectedPeerId = canonicalId;
            _selectedPeer = peer;
            RaisePropertyChanged(nameof(SelectedPeer));
        }

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

        if (_selectedPeerId == canonicalId)
        {
            _selectedPeer = peer;
            RaisePropertyChanged(nameof(SelectedPeer));
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

    private void RemoveDuplicatePeers(string canonicalId, PeerInfo peer)
    {
        var duplicateIds = _peers.Values
            .Where(existing => existing.Id != canonicalId && PeerMerge.SharesAddress(existing, peer))
            .Select(existing => existing.Id)
            .ToList();

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

                SelectedPeer = UpsertPeer(_selectedPeer with
                {
                    Address = NetworkEndpoints.SelectBestPeerAddress(normalizedRemote, addresses),
                    AllAddresses = addresses
                        .Select(NetworkEndpoints.NormalizeAddressString)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                });
            }
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
                                SelectedPeer = null;
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
                _streamingMicForPeer = false;
                _audioConnection.Disconnect();
                ConnectionPhase = ConnectionPhase.Idle;
                _errorMessage = null;
                UpdateDiscoveryScanning();
                UpdateFooterText();
                return;
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
            if (IsBufferLocked)
            {
                _latencySettings.SetTargetLatencyMs(_manualBufferMs);
            }
            else
            {
                _latencySettings.Reset();
                _manualBufferMs = AdaptiveLatencySettings.DefaultLatencyMs;
            }

            BufferMs = _latencySettings.TargetLatencyMs;
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
