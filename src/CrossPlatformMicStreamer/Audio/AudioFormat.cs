namespace CrossPlatformMicStreamer.Audio;

public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 1;

    public static int BytesPerSample(AudioBitDepth bitDepth) => bitDepth.BytesPerSample();

    public static int FrameBytes(int frameSamples, AudioBitDepth bitDepth) =>
        frameSamples * BytesPerSample(bitDepth) * Channels;
}
