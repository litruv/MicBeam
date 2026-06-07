using System.Security.Cryptography;
using System.Text;

namespace CrossPlatformMicStreamer.Network;

internal static class LocalIdentityFactory
{
    public static LocalIdentity Create()
    {
        var machineName = Environment.MachineName;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"CrossPlatformMicStreamer:{machineName}"));
        var id = Convert.ToHexString(bytes)[..16].ToLowerInvariant();

        return new LocalIdentity(id, machineName);
    }
}
