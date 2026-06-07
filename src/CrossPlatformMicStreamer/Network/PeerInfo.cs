namespace CrossPlatformMicStreamer.Network;

public sealed record PeerInfo(
    string Id,
    string Name,
    string Address,
    int TcpPort,
    DateTime LastSeenUtc,
    bool IsManual = false,
    string[]? AllAddresses = null,
    string? DiscoverySource = null,
    Audio.PeerInputDevice[]? InputDevices = null)
{
    public IReadOnlyList<string> ConnectAddresses
    {
        get
        {
            var addresses = new List<string> { Address };

            if (AllAddresses != null)
            {
                foreach (var candidate in AllAddresses)
                {
                    if (!addresses.Contains(candidate, StringComparer.Ordinal))
                    {
                        addresses.Add(candidate);
                    }
                }
            }

            return addresses;
        }
    }

    public string DisplayName
    {
        get
        {
            var suffix = IsManual ? " [manual]" : DiscoverySource != null ? $" [{DiscoverySource}]" : string.Empty;
            return $"{Name} ({Address}){suffix}";
        }
    }

    public override string ToString() => DisplayName;
}
