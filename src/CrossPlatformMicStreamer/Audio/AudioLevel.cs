using System.Runtime.InteropServices;

namespace CrossPlatformMicStreamer.Audio;

internal static class AudioLevel
{
    public static float PeakFromBuffer(ReadOnlySpan<byte> buffer, AudioBitDepth bitDepth) =>
        bitDepth switch
        {
            AudioBitDepth.Bits16 => PeakFromInt16(buffer),
            AudioBitDepth.Bits24 => PeakFromInt24(buffer),
            AudioBitDepth.Bits32 => PeakFromFloat32(buffer),
            _ => 0f,
        };

    public static float UpdatePeak(float current, float newPeak, float decay = 0.9f) =>
        Math.Clamp(Math.Max(newPeak, current * decay), 0f, 1f);

    private static float PeakFromFloat32(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < sizeof(float))
        {
            return 0f;
        }

        var samples = MemoryMarshal.Cast<byte, float>(buffer);
        var peak = 0f;

        foreach (var sample in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private static float PeakFromInt16(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < sizeof(short))
        {
            return 0f;
        }

        var samples = MemoryMarshal.Cast<byte, short>(buffer);
        var peak = 0f;

        foreach (var sample in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample / 32768f));
        }

        return Math.Clamp(peak, 0f, 1f);
    }

    private static float PeakFromInt24(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 3)
        {
            return 0f;
        }

        var peak = 0f;

        for (var i = 0; i + 2 < buffer.Length; i += 3)
        {
            var sample = buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16);
            if ((sample & 0x800000) != 0)
            {
                sample |= unchecked((int)0xFF000000);
            }

            peak = MathF.Max(peak, MathF.Abs(sample / 8388608f));
        }

        return Math.Clamp(peak, 0f, 1f);
    }
}
