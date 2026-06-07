using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;

namespace CrossPlatformMicStreamer.Network;

public sealed class AudioStreamConnection : IDisposable
{
    private const int MaxChunkSize = 1024 * 64;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _connectionLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _acceptTask;
    private NetworkStream? _stream;
    private Task? _readTask;
    private Task? _writeTask;
    private Task? _pingTask;
    private ChannelWriter<byte[]>? _outgoingWriter;
    private ChannelReader<byte[]>? _outgoingReader;
    private CancellationTokenSource? _connectionCts;
    private int _pendingSendFrames;
    private string? _connectedAddress;
    private TcpClient? _connectedClient;
    private double _networkRoundTripMs;
    private long _lastPingTimestamp;
    private int _connectionLostSignaled;

    public event Action<byte[]>? AudioReceived;
    public event Action<string>? StatusChanged;
    public event Action<string>? Connected;
    public event Action<string?>? ConnectionLost;
    public event Action<int>? RemoteInputDeviceRequested;
    public event Action<int>? RemoteLatencyChanged;
    public event Action<int>? RemoteBitDepthChanged;

    public double NetworkMs => _networkRoundTripMs * 0.5;

    public double SendQueueMs =>
        Volatile.Read(ref _pendingSendFrames) * _frameDurationMs;

    private double _frameDurationMs = 10;

    public bool IsConnected
    {
        get
        {
            lock (_connectionLock)
            {
                return _stream != null;
            }
        }
    }

    public AudioStreamConnection()
    {
        _listener = new TcpListener(IPAddress.Any, NetworkConstants.AudioPort);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    public void StartListening()
    {
        _listener.Start();
        _acceptTask = Task.Run(AcceptLoopAsync);
        StatusChanged?.Invoke($"Listening for audio on TCP {NetworkConstants.AudioPort}.");
        SessionLog.Write($"Listening on TCP {NetworkConstants.AudioPort}.");
    }

    public bool IsConnectedTo(string address)
    {
        lock (_connectionLock)
        {
            if (_stream == null || _connectedAddress == null)
            {
                return false;
            }

            var normalized = NetworkEndpoints.NormalizeAddressString(address);
            return string.Equals(_connectedAddress, normalized, StringComparison.Ordinal);
        }
    }

    public bool IsConnectedToPeer(PeerInfo peer)
    {
        var connectedAddress = ConnectedAddress;
        if (connectedAddress == null)
        {
            return false;
        }

        return peer.ConnectAddresses.Any(address =>
            string.Equals(
                NetworkEndpoints.NormalizeAddressString(address),
                connectedAddress,
                StringComparison.Ordinal));
    }

    public string? ConnectedAddress
    {
        get
        {
            lock (_connectionLock)
            {
                return _connectedAddress;
            }
        }
    }

    public async Task<bool> TryConnectToPeerAsync(PeerInfo peer, CancellationToken cancellationToken)
    {
        if (IsConnectedToPeer(peer))
        {
            return true;
        }

        foreach (var targetAddress in peer.ConnectAddresses)
        {
            if (IsConnectedTo(targetAddress))
            {
                return true;
            }

            var normalizedAddress = NetworkEndpoints.NormalizeAddressString(targetAddress);
            TcpClient client = new();
            client.NoDelay = true;

            try
            {
                await client.ConnectAsync(IPAddress.Parse(normalizedAddress), peer.TcpPort, cancellationToken);
            }
            catch (Exception ex)
            {
                client.Dispose();
                StatusChanged?.Invoke($"TCP connect to {normalizedAddress}:{peer.TcpPort} failed: {ex.Message}");
                SessionLog.Write($"TCP connect failed to {normalizedAddress}:{peer.TcpPort}: {ex.Message}");
                continue;
            }

            lock (_connectionLock)
            {
                if (_stream != null)
                {
                    client.Dispose();
                    return IsConnectedToPeer(peer);
                }
            }

            AttachStream(
                client.GetStream(),
                normalizedAddress,
                $"Connected to {peer.DisplayName} via {normalizedAddress}.",
                client);
            return true;
        }

        return false;
    }

    public void Disconnect()
    {
        Interlocked.Exchange(ref _connectionLostSignaled, 1);
        CancelConnectionTasks();
        CleanupConnection();
        StatusChanged?.Invoke("Disconnected.");
        SessionLog.Write("Disconnected intentionally.");
    }

    public void UpdateFrameDuration(double frameDurationMs)
    {
        _frameDurationMs = frameDurationMs;
    }

    public void EnqueueOutgoing(byte[] chunk)
    {
        if (_outgoingWriter == null)
        {
            return;
        }

        if (_outgoingWriter.TryWrite(chunk))
        {
            Interlocked.Increment(ref _pendingSendFrames);
        }
    }

    public async Task SendSelectInputDeviceAsync(int deviceIndex, CancellationToken cancellationToken)
    {
        NetworkStream? stream;
        lock (_connectionLock)
        {
            stream = _stream;
        }

        if (stream == null)
        {
            return;
        }

        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, deviceIndex);
        await WriteFrameAsync(stream, StreamProtocol.SelectInputFrameLength, payload, cancellationToken);
        SessionLog.Write($"Requested remote input device {deviceIndex}.");
    }

    public async Task SendSetLatencyAsync(int latencyMs, CancellationToken cancellationToken)
    {
        NetworkStream? stream;
        lock (_connectionLock)
        {
            stream = _stream;
        }

        if (stream == null)
        {
            return;
        }

        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, latencyMs);
        await WriteFrameAsync(stream, StreamProtocol.SetLatencyFrameLength, payload, cancellationToken);
        SessionLog.Write($"Synced latency {latencyMs} ms to peer.");
    }

    public async Task SendSetBitDepthAsync(int bitDepth, CancellationToken cancellationToken)
    {
        NetworkStream? stream;
        lock (_connectionLock)
        {
            stream = _stream;
        }

        if (stream == null)
        {
            return;
        }

        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, bitDepth);
        await WriteFrameAsync(stream, StreamProtocol.SetBitDepthFrameLength, payload, cancellationToken);
        SessionLog.Write($"Synced bit depth {bitDepth}-bit to peer.");
    }

    private void AttachStream(NetworkStream stream, string remoteAddress, string status, TcpClient client)
    {
        var normalizedAddress = NetworkEndpoints.NormalizeAddressString(remoteAddress);

        CancelConnectionTasks();

        lock (_connectionLock)
        {
            _stream?.Dispose();
            _connectedClient?.Dispose();
            _stream = stream;
            _connectedClient = client;
            _connectedAddress = normalizedAddress;
        }

        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var connectionToken = _connectionCts.Token;

        var channel = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(16)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        _outgoingWriter = channel.Writer;
        _outgoingReader = channel.Reader;
        Interlocked.Exchange(ref _pendingSendFrames, 0);
        Interlocked.Exchange(ref _connectionLostSignaled, 0);
        _networkRoundTripMs = 0;

        _readTask = Task.Run(() => ReadLoopAsync(stream, connectionToken));
        _writeTask = Task.Run(() => WriteLoopAsync(stream, channel.Reader, connectionToken));
        _pingTask = Task.Run(() => PingLoopAsync(stream, connectionToken));
        StatusChanged?.Invoke(status);
        Connected?.Invoke(normalizedAddress);
        SessionLog.Write($"Attached stream to {normalizedAddress}.");
    }

    private void CancelConnectionTasks()
    {
        try
        {
            _connectionCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _connectionCts?.Dispose();
        }
        catch
        {
        }

        _connectionCts = null;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient? client = null;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;

                var remote = client.Client.RemoteEndPoint as IPEndPoint;
                if (remote == null)
                {
                    client.Dispose();
                    continue;
                }

                var address = NetworkEndpoints.NormalizeAddress(remote.Address);

                lock (_connectionLock)
                {
                    if (_stream != null)
                    {
                        client.Dispose();
                        SessionLog.Write($"Rejected extra inbound connection from {address} (already connected).");
                        continue;
                    }
                }

                AttachStream(
                    client.GetStream(),
                    address,
                    $"Accepted connection from {address}:{remote.Port}.",
                    client);
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                client?.Dispose();
                StatusChanged?.Invoke($"Accept error: {ex.Message}");
                SessionLog.Write($"Accept error: {ex.Message}");
                await Task.Delay(250, _cts.Token);
            }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, lengthBuffer, cancellationToken))
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        SignalConnectionLost("remote closed read loop");
                    }

                    return;
                }

                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

                if (length == StreamProtocol.PingFrameLength)
                {
                    var payload = new byte[StreamProtocol.PingPayloadBytes];
                    if (!await ReadExactAsync(stream, payload, cancellationToken))
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            SignalConnectionLost("ping read failed");
                        }

                        return;
                    }

                    await WriteFrameAsync(stream, StreamProtocol.PongFrameLength, payload, cancellationToken);
                    continue;
                }

                if (length == StreamProtocol.PongFrameLength)
                {
                    var payload = new byte[StreamProtocol.PingPayloadBytes];
                    if (!await ReadExactAsync(stream, payload, cancellationToken))
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            SignalConnectionLost("pong read failed");
                        }

                        return;
                    }

                    var sentTimestamp = BinaryPrimitives.ReadInt64LittleEndian(payload);
                    if (sentTimestamp == Volatile.Read(ref _lastPingTimestamp))
                    {
                        var elapsedTicks = Stopwatch.GetTimestamp() - sentTimestamp;
                        _networkRoundTripMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                    }

                    continue;
                }

                if (length == StreamProtocol.SelectInputFrameLength)
                {
                    var payload = new byte[4];
                    if (!await ReadExactAsync(stream, payload, cancellationToken))
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            SignalConnectionLost("select-input read failed");
                        }

                        return;
                    }

                    var deviceIndex = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    RemoteInputDeviceRequested?.Invoke(deviceIndex);
                    continue;
                }

                if (length == StreamProtocol.SetLatencyFrameLength)
                {
                    var payload = new byte[4];
                    if (!await ReadExactAsync(stream, payload, cancellationToken))
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            SignalConnectionLost("set-latency read failed");
                        }

                        return;
                    }

                    var latencyMs = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    RemoteLatencyChanged?.Invoke(latencyMs);
                    continue;
                }

                if (length == StreamProtocol.SetBitDepthFrameLength)
                {
                    var payload = new byte[4];
                    if (!await ReadExactAsync(stream, payload, cancellationToken))
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            SignalConnectionLost("set-bit-depth read failed");
                        }

                        return;
                    }

                    var bitDepth = BinaryPrimitives.ReadInt32LittleEndian(payload);
                    RemoteBitDepthChanged?.Invoke(bitDepth);
                    continue;
                }

                if (length <= 0 || length > MaxChunkSize)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        SignalConnectionLost($"invalid frame length {length}");
                    }

                    return;
                }

                var audio = new byte[length];
                if (!await ReadExactAsync(stream, audio, cancellationToken))
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        SignalConnectionLost("audio read failed");
                    }

                    return;
                }

                AudioReceived?.Invoke(audio);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SignalConnectionLost($"read loop error: {ex.Message}");
            }
        }
    }

    private async Task WriteLoopAsync(
        NetworkStream stream,
        ChannelReader<byte[]> reader,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in reader.ReadAllAsync(cancellationToken))
            {
                await WriteFrameAsync(stream, chunk.Length, chunk, cancellationToken);
                Interlocked.Decrement(ref _pendingSendFrames);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SignalConnectionLost($"write loop error: {ex.Message}");
            }
        }
    }

    private async Task PingLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var payload = new byte[StreamProtocol.PingPayloadBytes];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(500, cancellationToken);

                var timestamp = Stopwatch.GetTimestamp();
                Volatile.Write(ref _lastPingTimestamp, timestamp);
                BinaryPrimitives.WriteInt64LittleEndian(payload, timestamp);
                await WriteFrameAsync(stream, StreamProtocol.PingFrameLength, payload, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SignalConnectionLost($"ping loop error: {ex.Message}");
            }
        }
    }

    private void SignalConnectionLost(string reason)
    {
        if (Interlocked.CompareExchange(ref _connectionLostSignaled, 1, 0) != 0)
        {
            return;
        }

        string? address;
        lock (_connectionLock)
        {
            address = _connectedAddress;
        }

        CancelConnectionTasks();
        CleanupConnection();
        ConnectionLost?.Invoke(address);
        StatusChanged?.Invoke($"Connection lost: {reason}");
        SessionLog.Write($"Connection lost from {address ?? "unknown"}: {reason}");
    }

    private void CleanupConnection()
    {
        lock (_connectionLock)
        {
            _stream?.Dispose();
            _stream = null;
            _connectedClient?.Dispose();
            _connectedClient = null;
            _outgoingWriter = null;
            _outgoingReader = null;
            _connectedAddress = null;
        }

        Interlocked.Exchange(ref _pendingSendFrames, 0);
        _networkRoundTripMs = 0;
    }

    private async Task WriteFrameAsync(
        NetworkStream stream,
        int length,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, length);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await stream.WriteAsync(lengthBuffer, cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        Disconnect();

        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(1));
            _readTask?.Wait(TimeSpan.FromSeconds(1));
            _writeTask?.Wait(TimeSpan.FromSeconds(1));
            _pingTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        _cts.Dispose();
    }
}
