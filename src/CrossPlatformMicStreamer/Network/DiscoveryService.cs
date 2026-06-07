using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CrossPlatformMicStreamer.Audio;

namespace CrossPlatformMicStreamer.Network;

public static class NetworkConstants
{
    public const int DiscoveryPort = 18240;
    public const int AudioPort = 18241;
}

public sealed record LocalIdentity(string Id, string Name);

public sealed class DiscoveryService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly LocalIdentity _identity;
    private readonly Socket _listener;
    private readonly MdnsDiscovery _mdnsDiscovery;
    private readonly ConcurrentDictionary<string, IPEndPoint> _knownPeerEndpoints = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveTask;
    private Task? _announceTask;
    private Task? _probeTask;
    private Task? _tailscaleProbeTask;
    private volatile bool _aggressiveScanningEnabled = true;

    public event Action<PeerInfo>? PeerDiscovered;
    public event Action<string>? StatusChanged;

    public DiscoveryService(LocalIdentity identity)
    {
        _identity = identity;
        _listener = CreateListenerSocket();
        _mdnsDiscovery = new MdnsDiscovery(identity);
        _mdnsDiscovery.PeerDiscovered += peer => _ = VerifyAndPublishPeerAsync(peer);
        _mdnsDiscovery.StatusChanged += message => StatusChanged?.Invoke(message);
    }

    public void Start()
    {
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _announceTask = Task.Run(AnnounceLoopAsync);
        _probeTask = Task.Run(SubnetProbeLoopAsync);
        _tailscaleProbeTask = Task.Run(TailscaleProbeLoopAsync);
        _mdnsDiscovery.Start();

        var interfaces = NetworkEndpoints.GetLocalNetworkInterfaces();
        var addressText = interfaces.Count == 0
            ? "unknown"
            : string.Join("; ", interfaces.Select(iface => $"{iface.Name}={iface.Address}/{iface.PrefixLength}"));

        var tailscalePeers = TailscaleDiscovery.GetOnlinePeerAddresses();
        var tailscaleText = tailscalePeers.Count == 0
            ? "none"
            : $"{tailscalePeers.Count} online peer(s)";

        StatusChanged?.Invoke(
            $"Discovery on UDP {NetworkConstants.DiscoveryPort}, mDNS, subnet sweep, Tailscale. " +
            $"Interfaces: {addressText}. Tailscale: {tailscaleText}.");
    }

    public void RegisterPeerAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed))
        {
            return;
        }

        _knownPeerEndpoints[NetworkEndpoints.NormalizeAddress(parsed)] =
            new IPEndPoint(parsed, NetworkConstants.DiscoveryPort);
    }

    public void SetAggressiveScanningEnabled(bool enabled)
    {
        _aggressiveScanningEnabled = enabled;
        _mdnsDiscovery.SetBrowsingEnabled(enabled);

        if (!enabled)
        {
            return;
        }

        _mdnsDiscovery.Refresh();
    }

    public void ProbeNow()
    {
        if (!_aggressiveScanningEnabled)
        {
            StatusChanged?.Invoke("Discovery refresh skipped while streaming.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var subnetTargets = await ProbeLocalSubnetsAsync(_cts.Token);
                var tailscaleTargets = await ProbeTailscalePeersAsync(_cts.Token);
                _mdnsDiscovery.Refresh();

                StatusChanged?.Invoke(
                    $"Discovery refresh sent: subnet={subnetTargets}, tailscale={tailscaleTargets}, mDNS query.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Discovery refresh failed: {ex.Message}");
            }
        });
    }

    private Socket CreateListenerSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };

        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, NetworkConstants.DiscoveryPort));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Discovery bind failed on UDP {NetworkConstants.DiscoveryPort}: {ex.Message}");
            throw;
        }

        foreach (var iface in NetworkEndpoints.GetLocalNetworkInterfaces())
        {
            try
            {
                socket.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.AddMembership,
                    new MulticastOption(
                        IPAddress.Parse(NetworkEndpoints.MulticastAddress),
                        IPAddress.Parse(iface.Address)));
            }
            catch
            {
            }
        }

        try
        {
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(NetworkEndpoints.MulticastAddress), IPAddress.Any));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Multicast join failed: {ex.Message}");
        }

        return socket;
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[4096];

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var received = await _listener.ReceiveAsync(buffer, SocketFlags.None, _cts.Token);
                var payload = Encoding.UTF8.GetString(buffer, 0, received);

                if (!TryParseAnnouncement(payload, out var announcement))
                {
                    continue;
                }

                if (announcement.Id == _identity.Id)
                {
                    continue;
                }

                var remote = _listener.RemoteEndPoint as IPEndPoint;
                if (remote == null)
                {
                    continue;
                }

                HandleDiscoveredPeer(
                    announcement.Id,
                    announcement.Name,
                    NetworkEndpoints.NormalizeAddress(remote.Address),
                    announcement.TcpPort,
                    announcement.Addresses,
                    ToPeerInputDevices(announcement.InputDevices),
                    "UDP");

                if (_aggressiveScanningEnabled)
                {
                    await SendAnnouncementToAsync(new IPEndPoint(remote.Address, NetworkConstants.DiscoveryPort));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Discovery receive error: {ex.Message}");
                await Task.Delay(250, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private void HandleDiscoveredPeer(
        string id,
        string name,
        string sourceAddress,
        int tcpPort,
        string[]? addresses,
        PeerInputDevice[]? inputDevices,
        string source)
    {
        if (!_aggressiveScanningEnabled)
        {
            return;
        }

        var normalizedAddresses = (addresses ?? [])
            .Select(NetworkEndpoints.NormalizeAddressString)
            .Append(sourceAddress)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var bestAddress = NetworkEndpoints.SelectBestPeerAddress(sourceAddress, normalizedAddresses);
        RegisterPeerEndpoints(bestAddress, normalizedAddresses);

        _ = VerifyAndPublishPeerAsync(new PeerInfo(
            id,
            name,
            bestAddress,
            tcpPort,
            DateTime.UtcNow,
            AllAddresses: normalizedAddresses,
            DiscoverySource: source,
            InputDevices: inputDevices));
    }

    private async Task VerifyAndPublishPeerAsync(PeerInfo peer)
    {
        try
        {
            if (!await PeerReachability.IsAudioPortOpenAsync(
                    peer.ConnectAddresses,
                    peer.TcpPort,
                    _cts.Token))
            {
                return;
            }

            PeerDiscovered?.Invoke(peer);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task AnnounceLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_aggressiveScanningEnabled)
                {
                    var sentTargets = await BroadcastAnnouncementAsync();

                    foreach (var target in _knownPeerEndpoints.Values)
                    {
                        if (await SendAnnouncementToAsync(target))
                        {
                            sentTargets++;
                        }
                    }

                    if (sentTargets == 0)
                    {
                        StatusChanged?.Invoke("Discovery announce failed: no usable network interfaces.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Discovery announce error: {ex.Message}");
            }

            try
            {
                await Task.Delay(
                    _aggressiveScanningEnabled ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(15),
                    _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SubnetProbeLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(1), _cts.Token);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_aggressiveScanningEnabled)
                {
                    await ProbeLocalSubnetsAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TailscaleProbeLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (_aggressiveScanningEnabled)
                {
                    await ProbeTailscalePeersAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<int> ProbeLocalSubnetsAsync(CancellationToken cancellationToken)
    {
        var sent = 0;

        foreach (var iface in NetworkEndpoints.GetLocalNetworkInterfaces())
        {
            foreach (var host in NetworkEndpoints.GetSubnetProbeTargets(iface))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (await SendAnnouncementToAsync(
                        new IPEndPoint(IPAddress.Parse(host), NetworkConstants.DiscoveryPort),
                        iface))
                {
                    sent++;
                }

                if (sent % 64 == 0)
                {
                    await Task.Delay(5, cancellationToken);
                }
            }
        }

        return sent;
    }

    private async Task<int> ProbeTailscalePeersAsync(CancellationToken cancellationToken)
    {
        var sent = 0;

        foreach (var peerAddress in TailscaleDiscovery.GetOnlinePeerAddresses())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var routeInterface = NetworkEndpoints.FindRouteInterface(IPAddress.Parse(peerAddress));
            if (await SendAnnouncementToAsync(
                    new IPEndPoint(IPAddress.Parse(peerAddress), NetworkConstants.DiscoveryPort),
                    routeInterface))
            {
                sent++;
            }
        }

        return sent;
    }

    private async Task<int> BroadcastAnnouncementAsync()
    {
        var sentTargets = 0;
        var bytes = CreateAnnouncementBytes();

        foreach (var (iface, target) in NetworkEndpoints.GetInterfaceBroadcastTargets(NetworkConstants.DiscoveryPort))
        {
            if (await SendFromInterfaceAsync(bytes, target, iface))
            {
                sentTargets++;
            }
        }

        foreach (var (iface, target) in NetworkEndpoints.GetInterfaceMulticastTargets(NetworkConstants.DiscoveryPort))
        {
            if (await SendFromInterfaceAsync(bytes, target, iface))
            {
                sentTargets++;
            }
        }

        return sentTargets;
    }

    private async Task<bool> SendAnnouncementToAsync(IPEndPoint target, LocalNetworkInterface? routeInterface = null)
    {
        return await SendFromInterfaceAsync(CreateAnnouncementBytes(), target, routeInterface);
    }

    private async Task<bool> SendFromInterfaceAsync(
        byte[] bytes,
        IPEndPoint target,
        LocalNetworkInterface? routeInterface)
    {
        routeInterface ??= NetworkEndpoints.FindRouteInterface(target.Address);

        if (routeInterface != null)
        {
            return await TrySendFromAddressAsync(bytes, target, routeInterface.Address);
        }

        var sent = false;

        foreach (var iface in NetworkEndpoints.GetLocalNetworkInterfaces())
        {
            if (await TrySendFromAddressAsync(bytes, target, iface.Address))
            {
                sent = true;
            }
        }

        if (!sent)
        {
            sent = await TrySendUnboundAsync(bytes, target);
        }

        return sent;
    }

    private async Task<bool> TrySendFromAddressAsync(byte[] bytes, IPEndPoint target, string localAddress)
    {
        try
        {
            using var socket = CreateSenderSocket();
            socket.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
            await socket.SendToAsync(bytes, target, _cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TrySendUnboundAsync(byte[] bytes, IPEndPoint target)
    {
        try
        {
            using var socket = CreateSenderSocket();
            await socket.SendToAsync(bytes, target, _cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Socket CreateSenderSocket()
    {
        return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };
    }

    private byte[] CreateAnnouncementBytes()
    {
        var inputDevices = PortAudioDeviceEnumerator.GetInputDevices()
            .Select(device => new AnnouncementDevice(device.Index, device.Name))
            .ToArray();

        var announcement = new AnnouncementMessage(
            _identity.Id,
            _identity.Name,
            NetworkConstants.AudioPort,
            NetworkEndpoints.GetLocalAddresses().ToArray(),
            inputDevices);

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(announcement, JsonOptions));
    }

    private static PeerInputDevice[]? ToPeerInputDevices(AnnouncementDevice[]? devices)
    {
        if (devices == null || devices.Length == 0)
        {
            return null;
        }

        return devices
            .Select(device => new PeerInputDevice(device.Index, device.Name))
            .ToArray();
    }

    private void RegisterPeerEndpoints(string primaryAddress, IEnumerable<string> addresses)
    {
        _knownPeerEndpoints[primaryAddress] =
            new IPEndPoint(IPAddress.Parse(primaryAddress), NetworkConstants.DiscoveryPort);

        foreach (var address in addresses)
        {
            if (!IPAddress.TryParse(address, out var parsed))
            {
                continue;
            }

            var normalized = NetworkEndpoints.NormalizeAddress(parsed);
            _knownPeerEndpoints[normalized] = new IPEndPoint(parsed, NetworkConstants.DiscoveryPort);
        }
    }

    private static bool TryParseAnnouncement(string payload, out AnnouncementMessage announcement)
    {
        announcement = default!;

        try
        {
            var parsed = JsonSerializer.Deserialize<AnnouncementMessage>(payload, JsonOptions);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.Id) || string.IsNullOrWhiteSpace(parsed.Name))
            {
                return false;
            }

            announcement = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        foreach (var task in new[] { _receiveTask, _announceTask, _probeTask, _tailscaleProbeTask })
        {
            try
            {
                task?.Wait(TimeSpan.FromSeconds(1));
            }
            catch
            {
            }
        }

        _listener.Dispose();
        _mdnsDiscovery.Dispose();
        _cts.Dispose();
    }

    private sealed record AnnouncementDevice(int Index, string Name);

    private sealed record AnnouncementMessage(
        string Id,
        string Name,
        int TcpPort,
        string[]? Addresses,
        AnnouncementDevice[]? InputDevices);
}
