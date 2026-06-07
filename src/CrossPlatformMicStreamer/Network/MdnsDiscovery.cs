using Makaretu.Dns;

namespace CrossPlatformMicStreamer.Network;

internal sealed class MdnsDiscovery : IDisposable
{
    public const string ServiceType = "_micstreamer._udp";

    private readonly LocalIdentity _identity;
    private readonly MulticastService _mdns = new();
    private readonly ServiceDiscovery _serviceDiscovery;
    private ServiceProfile? _profile;
    private bool _started;
    private bool _available;
    private bool _browsingEnabled = true;

    public bool IsAvailable => _available;

    public event Action<PeerInfo>? PeerDiscovered;
    public event Action<string>? StatusChanged;

    public MdnsDiscovery(LocalIdentity identity)
    {
        _identity = identity;
        _serviceDiscovery = new ServiceDiscovery(_mdns);
        _serviceDiscovery.ServiceInstanceDiscovered += OnServiceInstanceDiscovered;
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        try
        {
            _mdns.NetworkInterfaceDiscovered += (_, _) =>
            {
                if (_available && _browsingEnabled)
                {
                    _serviceDiscovery.QueryServiceInstances(ServiceType);
                }
            };

            _mdns.Start();

            var profile = BuildServiceProfile();
            _profile = profile;
            if (_serviceDiscovery.Probe(profile))
            {
                StatusChanged?.Invoke($"mDNS name conflict for {profile.FullyQualifiedName}; browsing only.");
            }
            else
            {
                _serviceDiscovery.Advertise(profile);
                _serviceDiscovery.Announce(profile);
            }

            _serviceDiscovery.QueryServiceInstances(ServiceType);
            _available = true;
            StatusChanged?.Invoke($"mDNS advertising {ServiceType} on all interfaces.");
        }
        catch (Exception ex)
        {
            _available = false;
            StatusChanged?.Invoke($"mDNS unavailable: {ex.Message}. UDP and Tailscale discovery still active.");
        }
    }

    public void SetBrowsingEnabled(bool enabled)
    {
        _browsingEnabled = enabled;
    }

    public void Refresh()
    {
        if (!_started || !_available || !_browsingEnabled)
        {
            return;
        }

        _serviceDiscovery.QueryServiceInstances(ServiceType);
    }

    private ServiceProfile BuildServiceProfile()
    {
        var profile = new ServiceProfile(
            SanitizeInstanceName(_identity.Name),
            ServiceType,
            NetworkConstants.DiscoveryPort);

        profile.AddProperty("id", _identity.Id);
        profile.AddProperty("name", _identity.Name);
        profile.AddProperty("tcp", NetworkConstants.AudioPort.ToString());

        return profile;
    }

    private async void OnServiceInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs args)
    {
        if (!_browsingEnabled)
        {
            return;
        }

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var query = new Message();
            query.Questions.Add(new Question
            {
                Name = args.ServiceInstanceName,
                Type = DnsType.ANY,
            });

            var response = await _mdns.ResolveAsync(query, cancellation.Token);
            if (!TryParsePeer(response, out var peer))
            {
                return;
            }

            if (peer.Id == _identity.Id)
            {
                return;
            }

            PeerDiscovered?.Invoke(peer);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"mDNS resolve error: {ex.Message}");
        }
    }

    private static bool TryParsePeer(Message response, out PeerInfo peer)
    {
        peer = default!;

        string? id = null;
        string? name = null;
        var tcpPort = NetworkConstants.AudioPort;
        var addresses = new List<string>();

        foreach (var record in response.Answers.Concat(response.AdditionalRecords))
        {
            switch (record)
            {
                case TXTRecord txt:
                    foreach (var property in txt.Strings)
                    {
                        var separator = property.IndexOf('=');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        var key = property[..separator];
                        var value = property[(separator + 1)..];

                        switch (key)
                        {
                            case "id":
                                id = value;
                                break;
                            case "name":
                                name = value;
                                break;
                            case "tcp" when int.TryParse(value, out var parsedPort):
                                tcpPort = parsedPort;
                                break;
                        }
                    }

                    break;
                case ARecord aRecord:
                    addresses.Add(NetworkEndpoints.NormalizeAddress(aRecord.Address));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || addresses.Count == 0)
        {
            return false;
        }

        var normalizedAddresses = addresses
            .Select(NetworkEndpoints.NormalizeAddressString)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var bestAddress = NetworkEndpoints.SelectBestPeerAddress(normalizedAddresses[0], normalizedAddresses);

        peer = new PeerInfo(
            id,
            name,
            bestAddress,
            tcpPort,
            DateTime.UtcNow,
            AllAddresses: normalizedAddresses,
            DiscoverySource: "mDNS");

        return true;
    }

    private static string SanitizeInstanceName(string name)
    {
        var sanitized = new string(name
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized) ? "micstreamer" : sanitized;
    }

    public void Dispose()
    {
        _serviceDiscovery.ServiceInstanceDiscovered -= OnServiceInstanceDiscovered;

        if (_profile != null && _available)
        {
            try
            {
                _serviceDiscovery.Unadvertise(_profile);
            }
            catch
            {
            }
        }

        try
        {
            _serviceDiscovery.Dispose();
        }
        catch
        {
        }

        if (_started)
        {
            try
            {
                _mdns.Dispose();
            }
            catch
            {
            }
        }
    }
}
