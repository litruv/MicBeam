using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrossPlatformMicStreamer;

public sealed class UiPreferences
{
    public string SessionMode { get; set; } = "Receive";

    public string? PeerId { get; set; }

    public string? PeerAddress { get; set; }

    public string? PeerName { get; set; }

    public int? InputDeviceIndex { get; set; }

    public string? InputDeviceName { get; set; }

    public int? OutputDeviceIndex { get; set; }

    public string? OutputDeviceName { get; set; }

    public int BitDepth { get; set; } = 32;

    public int ManualBufferMs { get; set; } = Audio.AdaptiveLatencySettings.DefaultLatencyMs;

    public bool IsBufferLocked { get; set; }

    public bool AutoConnect { get; set; } = true;
}

internal static class UiPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static string PreferencesPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppBranding.ProductName,
            "preferences.json");

    public static UiPreferences Load()
    {
        try
        {
            if (!File.Exists(PreferencesPath))
            {
                return new UiPreferences();
            }

            var json = File.ReadAllText(PreferencesPath);
            var preferences = JsonSerializer.Deserialize<UiPreferences>(json, JsonOptions) ?? new UiPreferences();
            SessionLog.Write($"Loaded preferences from {PreferencesPath}");
            return preferences;
        }
        catch (Exception ex)
        {
            SessionLog.Write($"Failed to load preferences from {PreferencesPath}: {ex.Message}");
            return new UiPreferences();
        }
    }

    public static void Save(UiPreferences preferences)
    {
        try
        {
            var directory = Path.GetDirectoryName(PreferencesPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(preferences, JsonOptions);
            var tempPath = PreferencesPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, PreferencesPath, overwrite: true);
            SessionLog.Write($"Saved preferences to {PreferencesPath}");
        }
        catch (Exception ex)
        {
            SessionLog.Write($"Failed to save preferences to {PreferencesPath}: {ex.Message}");
        }
    }
}
