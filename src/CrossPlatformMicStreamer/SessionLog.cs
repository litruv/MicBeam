namespace CrossPlatformMicStreamer;

internal static class SessionLog
{
    public static void Write(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                AppBranding.ProductName);

            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, "session.log");
            File.AppendAllText(path, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
