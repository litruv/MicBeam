namespace CrossPlatformMicStreamer.Audio;

public sealed class AdaptiveLatencySettings
{
    public const int StepMs = 5;
    public const int MinLatencyMs = 5;
    public const int DefaultLatencyMs = 10;
    public const int MaxLatencyMs = 120;

    private int _targetLatencyMs = DefaultLatencyMs;

    public int Level => (_targetLatencyMs - MinLatencyMs) / StepMs;

    public int MaxLevel => (MaxLatencyMs - MinLatencyMs) / StepMs;

    public bool IsAtMax => _targetLatencyMs >= MaxLatencyMs;

    public bool IsAtMin => _targetLatencyMs <= MinLatencyMs;

    public int StableIntervalsBeforeDecrease => 6;

    public int EvaluationIntervalMs => 2000;

    public int GracePeriodAfterRestartSeconds => 5;

    public int MinUnderrunsToIncrease => 4;

    public int MinSaturatedToIncrease => 3;

    public int BadIntervalsBeforeIncrease => 2;

    public AudioBitDepth BitDepth { get; set; } = AudioBitDepth.Bits32;

    public bool NeedsMoreBuffer(int underruns, int saturated, int drops) =>
        underruns >= MinUnderrunsToIncrease ||
        saturated >= MinSaturatedToIncrease ||
        drops >= MinUnderrunsToIncrease;

    public int TargetLatencyMs => _targetLatencyMs;

    public int MaxQueuedFrames => QueueForLatencyMs(_targetLatencyMs);

    public int MaxSendQueueFrames => QueueForLatencyMs(_targetLatencyMs);

    public int FrameSamples => AudioFormat.SampleRate * TargetLatencyMs / 1000;

    public double FrameDurationMs => TargetLatencyMs;

    public double TargetLatencySeconds => TargetLatencyMs / 1000.0;

    public int ChunkByteCount =>
        AudioFormat.FrameBytes(FrameSamples, BitDepth);

    public string LevelLabel => $"auto {TargetLatencyMs}/{MaxLatencyMs} ms";

    public bool TryIncrease()
    {
        if (IsAtMax)
        {
            return false;
        }

        _targetLatencyMs = Math.Min(_targetLatencyMs + StepMs, MaxLatencyMs);
        return true;
    }

    public bool TryDecrease()
    {
        if (IsAtMin)
        {
            return false;
        }

        _targetLatencyMs = Math.Max(_targetLatencyMs - StepMs, MinLatencyMs);
        return true;
    }

    public void Reset()
    {
        _targetLatencyMs = DefaultLatencyMs;
    }

    public void SetTargetLatencyMs(int latencyMs)
    {
        _targetLatencyMs = Math.Clamp(latencyMs, MinLatencyMs, MaxLatencyMs);
    }

    private static int QueueForLatencyMs(int latencyMs) =>
        latencyMs <= 10 ? 3 :
        latencyMs <= 20 ? 5 :
        latencyMs <= 40 ? 8 :
        latencyMs <= 80 ? 12 : 16;
}
