namespace CrossPlatformMicStreamer.Network;

internal static class PeerMerge
{
    public static bool SharesAddress(PeerInfo left, PeerInfo right)
    {
        var leftAddresses = left.ConnectAddresses
            .Select(NetworkEndpoints.NormalizeAddressString)
            .ToHashSet(StringComparer.Ordinal);

        return right.ConnectAddresses
            .Any(address => leftAddresses.Contains(NetworkEndpoints.NormalizeAddressString(address)));
    }

    public static string ChooseCanonicalId(PeerInfo existing, PeerInfo incoming)
    {
        if (existing.IsManual && !incoming.IsManual)
        {
            return incoming.Id;
        }

        if (!existing.IsManual)
        {
            return existing.Id;
        }

        return incoming.Id;
    }

    public static PeerInfo Merge(PeerInfo existing, PeerInfo incoming)
    {
        var mergedAddresses = existing.AllAddresses?
            .Concat(incoming.AllAddresses ?? [])
            .Append(existing.Address)
            .Append(incoming.Address)
            .Select(NetworkEndpoints.NormalizeAddressString)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var bestAddress = NetworkEndpoints.SelectBestPeerAddress(incoming.Address, mergedAddresses);
        var discoverySource = existing.DiscoverySource == incoming.DiscoverySource
            ? existing.DiscoverySource
            : string.Join(
                '+',
                new[] { existing.DiscoverySource, incoming.DiscoverySource }
                    .Where(source => !string.IsNullOrWhiteSpace(source))
                    .Distinct(StringComparer.OrdinalIgnoreCase));

        return incoming with
        {
            Name = string.IsNullOrWhiteSpace(incoming.Name) ? existing.Name : incoming.Name,
            Address = bestAddress,
            AllAddresses = mergedAddresses,
            IsManual = existing.IsManual && incoming.IsManual,
            DiscoverySource = discoverySource,
            InputDevices = incoming.InputDevices is { Length: > 0 }
                ? incoming.InputDevices
                : existing.InputDevices,
            LastSeenUtc = DateTime.UtcNow,
        };
    }
}
