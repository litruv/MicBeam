namespace CrossPlatformMicStreamer.Audio;

public sealed record AudioDeviceInfo(int Index, string Name, bool IsInput)
{
    public override string ToString() => Name;
}
