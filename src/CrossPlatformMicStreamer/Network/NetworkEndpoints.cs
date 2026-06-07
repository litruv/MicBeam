using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CrossPlatformMicStreamer.Network;

internal sealed record LocalNetworkInterface(
    string Name,
    string Address,
    IPAddress NetworkAddress,
    IPAddress Mask,
    IPAddress BroadcastAddress,
    int PrefixLength,
    NetworkInterfaceType InterfaceType)
{
    public bool IsPointToPoint => PrefixLength >= 31;

    public bool IsLikelyTailscale =>
        Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
        NetworkEndpoints.IsTailscaleCgNatAddress(Address);
}

internal static class NetworkEndpoints
{
    public const string MulticastAddress = "239.255.77.77";
    private const int MaxSubnetSweepHosts = 512;

    public static IReadOnlyList<LocalNetworkInterface> GetLocalNetworkInterfaces()
    {
        var interfaces = new List<LocalNetworkInterface>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork ||
                    unicast.IPv4Mask == null)
                {
                    continue;
                }

                var prefixLength = unicast.PrefixLength > 0
                    ? unicast.PrefixLength
                    : MaskToPrefixLength(unicast.IPv4Mask);

                interfaces.Add(new LocalNetworkInterface(
                    networkInterface.Name,
                    NormalizeAddress(unicast.Address),
                    GetNetworkAddress(unicast.Address, unicast.IPv4Mask),
                    unicast.IPv4Mask,
                    GetBroadcastAddress(unicast.Address, unicast.IPv4Mask),
                    prefixLength,
                    networkInterface.NetworkInterfaceType));
            }
        }

        return interfaces;
    }

    public static IReadOnlyList<string> GetLocalAddresses()
    {
        return GetLocalNetworkInterfaces()
            .Select(iface => iface.Address)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static IEnumerable<string> GetSubnetProbeTargets(LocalNetworkInterface iface)
    {
        if (iface.IsPointToPoint)
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var host in GetHostAddressesForInterface(iface))
        {
            if (string.Equals(host, iface.Address, StringComparison.Ordinal) || !seen.Add(host))
            {
                continue;
            }

            yield return host;
        }
    }

    public static IEnumerable<string> GetAllSubnetProbeTargets()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var iface in GetLocalNetworkInterfaces())
        {
            foreach (var host in GetSubnetProbeTargets(iface))
            {
                if (seen.Add(host))
                {
                    yield return host;
                }
            }
        }
    }

    public static IEnumerable<(LocalNetworkInterface Interface, IPEndPoint Target)> GetInterfaceBroadcastTargets(int port)
    {
        foreach (var iface in GetLocalNetworkInterfaces())
        {
            if (iface.IsPointToPoint)
            {
                continue;
            }

            yield return (iface, new IPEndPoint(iface.BroadcastAddress, port));
        }
    }

    public static IEnumerable<(LocalNetworkInterface Interface, IPEndPoint Target)> GetInterfaceMulticastTargets(int port)
    {
        foreach (var iface in GetLocalNetworkInterfaces())
        {
            yield return (iface, new IPEndPoint(IPAddress.Parse(MulticastAddress), port));
        }
    }

    public static LocalNetworkInterface? FindRouteInterface(IPAddress target)
    {
        foreach (var iface in GetLocalNetworkInterfaces())
        {
            if (IsAddressOnInterface(target, iface))
            {
                return iface;
            }
        }

        return null;
    }

    public static bool IsAddressOnInterface(IPAddress address, LocalNetworkInterface iface)
    {
        if (iface.IsPointToPoint)
        {
            return address.Equals(IPAddress.Parse(iface.Address));
        }

        var targetBytes = address.GetAddressBytes();
        var networkBytes = iface.NetworkAddress.GetAddressBytes();
        var maskBytes = iface.Mask.GetAddressBytes();

        for (var i = 0; i < 4; i++)
        {
            if ((targetBytes[i] & maskBytes[i]) != networkBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    public static string SelectBestPeerAddress(string sourceAddress, IEnumerable<string>? announcedAddresses)
    {
        var candidates = new List<string> { NormalizeAddressString(sourceAddress) };

        if (announcedAddresses != null)
        {
            foreach (var address in announcedAddresses)
            {
                if (string.IsNullOrWhiteSpace(address))
                {
                    continue;
                }

                var normalized = NormalizeAddressString(address);
                if (!candidates.Contains(normalized, StringComparer.Ordinal))
                {
                    candidates.Add(normalized);
                }
            }
        }

        foreach (var iface in GetLocalNetworkInterfaces())
        {
            foreach (var candidate in candidates)
            {
                if (!IPAddress.TryParse(candidate, out var parsed))
                {
                    continue;
                }

                if (IsAddressOnInterface(parsed, iface))
                {
                    return candidate;
                }
            }
        }

        return candidates[0];
    }

    public static bool IsTailscaleCgNatAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var parsed))
        {
            return false;
        }

        var bytes = parsed.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    public static string? GetPrimaryLocalAddress()
    {
        return GetLocalAddresses().FirstOrDefault();
    }

    public static string NormalizeAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        return address.ToString();
    }

    public static string NormalizeAddressString(string address)
    {
        return IPAddress.TryParse(address, out var parsed)
            ? NormalizeAddress(parsed)
            : address;
    }

    public static int CompareAddresses(string left, string right)
    {
        if (!IPAddress.TryParse(left, out var leftAddress) ||
            !IPAddress.TryParse(right, out var rightAddress) ||
            leftAddress.AddressFamily != AddressFamily.InterNetwork ||
            rightAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return string.CompareOrdinal(left, right);
        }

        var leftBytes = leftAddress.GetAddressBytes();
        var rightBytes = rightAddress.GetAddressBytes();

        for (var i = 0; i < 4; i++)
        {
            var compare = leftBytes[i].CompareTo(rightBytes[i]);
            if (compare != 0)
            {
                return compare;
            }
        }

        return 0;
    }

    private static IEnumerable<string> GetHostAddressesForInterface(LocalNetworkInterface iface)
    {
        var hostCount = GetHostCount(iface.PrefixLength);
        if (hostCount <= 0)
        {
            yield break;
        }

        if (hostCount > MaxSubnetSweepHosts)
        {
            foreach (var host in GetLocalSliceHosts(iface))
            {
                yield return host;
            }

            yield break;
        }

        var networkBytes = iface.NetworkAddress.GetAddressBytes();
        var hostBits = 32 - iface.PrefixLength;
        var totalHosts = 1 << hostBits;

        for (var offset = 1; offset < totalHosts - 1; offset++)
        {
            var hostValue =
                ((networkBytes[0] << 24) |
                 (networkBytes[1] << 16) |
                 (networkBytes[2] << 8) |
                 networkBytes[3]) + offset;

            yield return FormatHostAddress(hostValue);
        }
    }

    private static IEnumerable<string> GetLocalSliceHosts(LocalNetworkInterface iface)
    {
        if (!IPAddress.TryParse(iface.Address, out var localAddress))
        {
            yield break;
        }

        var localBytes = localAddress.GetAddressBytes();

        for (var host = 1; host <= 254; host++)
        {
            if (host == localBytes[3])
            {
                continue;
            }

            yield return $"{localBytes[0]}.{localBytes[1]}.{localBytes[2]}.{host}";
        }
    }

    private static int GetHostCount(int prefixLength)
    {
        if (prefixLength >= 31)
        {
            return 0;
        }

        var hostBits = 32 - prefixLength;
        var totalHosts = 1 << hostBits;
        return Math.Max(totalHosts - 2, 0);
    }

    private static string FormatHostAddress(int hostValue)
    {
        return string.Join(
            '.',
            (byte)((hostValue >> 24) & 255),
            (byte)((hostValue >> 16) & 255),
            (byte)((hostValue >> 8) & 255),
            (byte)(hostValue & 255));
    }

    private static int MaskToPrefixLength(IPAddress mask)
    {
        var bits = 0;
        foreach (var value in mask.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) == 0)
                {
                    return bits;
                }

                bits++;
            }
        }

        return bits;
    }

    private static IPAddress GetNetworkAddress(IPAddress address, IPAddress mask)
    {
        var ipBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        return new IPAddress(
        [
            (byte)(ipBytes[0] & maskBytes[0]),
            (byte)(ipBytes[1] & maskBytes[1]),
            (byte)(ipBytes[2] & maskBytes[2]),
            (byte)(ipBytes[3] & maskBytes[3]),
        ]);
    }

    private static IPAddress GetBroadcastAddress(IPAddress address, IPAddress mask)
    {
        var ipBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        return new IPAddress(
        [
            (byte)(ipBytes[0] | (maskBytes[0] ^ 255)),
            (byte)(ipBytes[1] | (maskBytes[1] ^ 255)),
            (byte)(ipBytes[2] | (maskBytes[2] ^ 255)),
            (byte)(ipBytes[3] | (maskBytes[3] ^ 255)),
        ]);
    }
}
