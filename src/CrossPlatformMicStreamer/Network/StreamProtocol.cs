namespace CrossPlatformMicStreamer.Network;

public static class StreamProtocol
{
    public const int PingFrameLength = -1;
    public const int PongFrameLength = -2;
    public const int SelectInputFrameLength = -3;
    public const int SetLatencyFrameLength = -4;
    public const int SetBitDepthFrameLength = -5;
    public const int PingPayloadBytes = 8;
}
