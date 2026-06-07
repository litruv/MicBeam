using PortAudioSharp;

namespace CrossPlatformMicStreamer.Audio;

public enum AudioBitDepth
{
    Bits16 = 16,
    Bits24 = 24,
    Bits32 = 32,
}

public static class AudioBitDepthExtensions
{
    public static int BytesPerSample(this AudioBitDepth bitDepth) =>
        bitDepth switch
        {
            AudioBitDepth.Bits16 => 2,
            AudioBitDepth.Bits24 => 3,
            AudioBitDepth.Bits32 => 4,
            _ => 4,
        };

    public static SampleFormat ToPortAudioFormat(this AudioBitDepth bitDepth) =>
        bitDepth switch
        {
            AudioBitDepth.Bits16 => SampleFormat.Int16,
            AudioBitDepth.Bits24 => SampleFormat.Int24,
            AudioBitDepth.Bits32 => SampleFormat.Float32,
            _ => SampleFormat.Float32,
        };

    public static string GetLabel(this AudioBitDepth bitDepth) =>
        bitDepth switch
        {
            AudioBitDepth.Bits16 => "16-bit",
            AudioBitDepth.Bits24 => "24-bit",
            AudioBitDepth.Bits32 => "32-bit float",
            _ => bitDepth.ToString(),
        };

    public static AudioBitDepth ParseWireValue(int value) =>
        value switch
        {
            16 => AudioBitDepth.Bits16,
            24 => AudioBitDepth.Bits24,
            32 => AudioBitDepth.Bits32,
            _ => AudioBitDepth.Bits32,
        };

    public static int ToWireValue(this AudioBitDepth bitDepth) => (int)bitDepth;
}
