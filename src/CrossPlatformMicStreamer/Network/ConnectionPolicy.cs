namespace CrossPlatformMicStreamer.Network;

internal static class ConnectionPolicy
{
    public static bool ShouldInitiateConnection(string localId, string peerId) =>
        string.Compare(localId, peerId, StringComparison.Ordinal) > 0;
}
