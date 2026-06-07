using System.Net;
using System.Net.Sockets;

namespace CrossPlatformMicStreamer.Network;

internal static class PeerReachability
{
    public static async Task<bool> IsAudioPortOpenAsync(
        IEnumerable<string> addresses,
        int port,
        CancellationToken cancellationToken,
        int timeoutMs = 750)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeoutMs);

        foreach (var address in addresses)
        {
            if (!IPAddress.TryParse(address, out var parsed))
            {
                continue;
            }

            TcpClient? client = null;

            try
            {
                client = new TcpClient();
                await client.ConnectAsync(parsed, port, timeoutCts.Token);
                return true;
            }
            catch
            {
            }
            finally
            {
                client?.Dispose();
            }
        }

        return false;
    }
}
