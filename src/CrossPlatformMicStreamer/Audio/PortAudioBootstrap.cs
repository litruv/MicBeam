using PortAudioSharp;

namespace CrossPlatformMicStreamer.Audio;

public static class PortAudioBootstrap
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            ConfigureLinuxEnvironment();
        }

        PortAudio.Initialize();
        _initialized = true;
    }

    public static void Terminate()
    {
        if (!_initialized)
        {
            return;
        }

        PortAudio.Terminate();
        _initialized = false;
    }

    public static bool IsLikelyJackOnlyDevice(string deviceName)
    {
        if (deviceName.Contains("pulse", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("pipewire", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return deviceName.Contains("jack", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPreferredLinuxDevice(string deviceName)
    {
        return deviceName.Contains("pulse", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("pipewire", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Equals("default", StringComparison.OrdinalIgnoreCase) ||
               deviceName.Contains("default (", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsUsableLinuxDevice(DeviceInfo info, int deviceIndex, bool hasPreferredDevices)
    {
        if (IsLikelyJackOnlyDevice(info.name))
        {
            return false;
        }

        if (IsPreferredLinuxDevice(info.name))
        {
            return true;
        }

        if (hasPreferredDevices)
        {
            return false;
        }

        return deviceIndex == PortAudio.DefaultInputDevice ||
               deviceIndex == PortAudio.DefaultOutputDevice;
    }

    private static void ConfigureLinuxEnvironment()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDir))
        {
            Environment.SetEnvironmentVariable("PULSE_RUNTIME_PATH", $"{runtimeDir}/pulse");
            Environment.SetEnvironmentVariable(
                "PULSE_SERVER",
                Environment.GetEnvironmentVariable("PULSE_SERVER") ?? $"unix:{runtimeDir}/pulse/native");
        }

        Environment.SetEnvironmentVariable("JACK_NO_START_SERVER", "1");
        Environment.SetEnvironmentVariable("JACK_NO_AUDIO_RESERVATION", "1");
        Environment.SetEnvironmentVariable("JACK_PROMISCUOUS_SERVER", string.Empty);
        Environment.SetEnvironmentVariable("ALSA_LOG_LEVEL", "0");
    }
}
