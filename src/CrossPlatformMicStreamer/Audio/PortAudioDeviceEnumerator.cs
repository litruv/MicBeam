using PortAudioSharp;

namespace CrossPlatformMicStreamer.Audio;

public static class PortAudioDeviceEnumerator
{
    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        return EnumerateDevices(
            device => device.maxInputChannels > 0,
            PortAudio.DefaultInputDevice);
    }

    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        return EnumerateDevices(
            device => device.maxOutputChannels > 0,
            PortAudio.DefaultOutputDevice);
    }

    public static AudioDeviceInfo? GetDefaultInputDevice()
    {
        return SelectDefaultDevice(GetInputDevices(), PortAudio.DefaultInputDevice);
    }

    public static AudioDeviceInfo? GetDefaultOutputDevice()
    {
        return SelectDefaultDevice(GetOutputDevices(), PortAudio.DefaultOutputDevice);
    }

    private static AudioDeviceInfo? SelectDefaultDevice(
        IReadOnlyList<AudioDeviceInfo> devices,
        int defaultDeviceIndex)
    {
        if (devices.Count == 0)
        {
            return null;
        }

        return devices.FirstOrDefault(device => device.Index == defaultDeviceIndex)
            ?? devices[0];
    }

    private static List<AudioDeviceInfo> EnumerateDevices(
        Func<DeviceInfo, bool> predicate,
        int defaultDeviceIndex)
    {
        PortAudioBootstrap.EnsureInitialized();

        var raw = new List<(int Index, string Name, bool IsInput)>();
        var hasPreferredLinuxDevices = false;

        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (!predicate(info))
            {
                continue;
            }

            if (OperatingSystem.IsLinux())
            {
                if (PortAudioBootstrap.IsPreferredLinuxDevice(info.name))
                {
                    hasPreferredLinuxDevices = true;
                }
            }
        }

        for (var i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            if (!predicate(info))
            {
                continue;
            }

            if (OperatingSystem.IsLinux() &&
                !PortAudioBootstrap.IsUsableLinuxDevice(info, i, hasPreferredLinuxDevices))
            {
                continue;
            }

            raw.Add((i, info.name, info.maxInputChannels > 0));
        }

        var duplicateNames = raw
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return raw
            .Select(entry =>
            {
                var label = duplicateNames.Contains(entry.Name)
                    ? $"{entry.Name} [#{entry.Index}]"
                    : entry.Name;

                if (entry.Index == defaultDeviceIndex)
                {
                    label += " (default)";
                }

                return new AudioDeviceInfo(entry.Index, label, entry.IsInput);
            })
            .OrderByDescending(device =>
                OperatingSystem.IsLinux() &&
                PortAudioBootstrap.IsPreferredLinuxDevice(PortAudio.GetDeviceInfo(device.Index).name))
            .ThenByDescending(device => device.Index == defaultDeviceIndex)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
