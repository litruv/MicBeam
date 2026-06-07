using System.Diagnostics;
using System.Text.Json;

namespace CrossPlatformMicStreamer.Network;

internal static class TailscaleDiscovery
{
    public static IReadOnlyList<string> GetOnlinePeerAddresses()
    {
        var output = TryRunTailscaleStatusJson();
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var addresses = new HashSet<string>(StringComparer.Ordinal);

            AddTailscaleAddresses(document.RootElement, "Self", addresses, requireOnline: false);

            if (document.RootElement.TryGetProperty("Peer", out var peers))
            {
                foreach (var peer in peers.EnumerateObject())
                {
                    AddTailscaleAddresses(peer.Value, peer.Name, addresses, requireOnline: true);
                }
            }

            return addresses
                .Where(address => !NetworkEndpoints.GetLocalAddresses().Contains(address, StringComparer.Ordinal))
                .OrderBy(address => address, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void AddTailscaleAddresses(
        JsonElement node,
        string nodeName,
        ISet<string> addresses,
        bool requireOnline)
    {
        if (requireOnline &&
            node.TryGetProperty("Online", out var onlineProperty) &&
            onlineProperty.ValueKind == JsonValueKind.False)
        {
            return;
        }

        if (!node.TryGetProperty("TailscaleIPs", out var tailscaleIps))
        {
            return;
        }

        foreach (var ipElement in tailscaleIps.EnumerateArray())
        {
            var ip = ipElement.GetString();
            if (string.IsNullOrWhiteSpace(ip) ||
                !System.Net.IPAddress.TryParse(ip, out var parsed) ||
                parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                continue;
            }

            addresses.Add(NetworkEndpoints.NormalizeAddress(parsed));
        }
    }

    private static string? TryRunTailscaleStatusJson()
    {
        foreach (var fileName in new[] { "tailscale", "tailscale.exe" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = "status --json",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                if (process == null)
                {
                    continue;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(TimeSpan.FromSeconds(5));

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    return output;
                }
            }
            catch
            {
            }
        }

        return null;
    }
}
