using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PortAudioSharp;

namespace CrossPlatformMicStreamer.Audio;

public sealed class MicrophoneCapture : IDisposable
{
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private PortAudioSharp.Stream? _stream;
    private int _droppedChunks;
    private float _meterLevel;
    private AdaptiveLatencySettings? _settings;

    public float MeterLevel => Volatile.Read(ref _meterLevel);

    public double QueuedDurationMs =>
        _queue.Count * (_settings?.FrameDurationMs ?? 10);

    public int ConsumeDroppedChunkCount()
    {
        return Interlocked.Exchange(ref _droppedChunks, 0);
    }

    public int PeekDroppedChunkCount() => Volatile.Read(ref _droppedChunks);

    public void Start(int deviceIndex, AdaptiveLatencySettings settings)
    {
        Stop();
        _settings = settings;
        Volatile.Write(ref _meterLevel, 0f);
        var bitDepth = settings.BitDepth;
        var bytesPerFrame = AudioFormat.FrameBytes(1, bitDepth);

        var info = PortAudio.GetDeviceInfo(deviceIndex);
        var parameters = new StreamParameters
        {
            device = deviceIndex,
            channelCount = AudioFormat.Channels,
            sampleFormat = bitDepth.ToPortAudioFormat(),
            suggestedLatency = Math.Min(info.defaultLowInputLatency, settings.TargetLatencySeconds),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        PortAudioSharp.Stream.Callback callback = (IntPtr input, IntPtr _, uint frameCount,
            ref StreamCallbackTimeInfo __, StreamCallbackFlags ___, IntPtr ____) =>
        {
            if (input == IntPtr.Zero || frameCount == 0 || _settings == null)
            {
                return StreamCallbackResult.Continue;
            }

            var byteCount = (int)frameCount * bytesPerFrame;
            var buffer = new byte[byteCount];
            Marshal.Copy(input, buffer, 0, byteCount);

            if (_queue.Count >= _settings.MaxQueuedFrames)
            {
                _queue.TryDequeue(out byte[]? _);
                Interlocked.Increment(ref _droppedChunks);
            }

            _queue.Enqueue(buffer);
            Volatile.Write(
                ref _meterLevel,
                AudioLevel.UpdatePeak(
                    Volatile.Read(ref _meterLevel),
                    AudioLevel.PeakFromBuffer(buffer, _settings.BitDepth)));
            return StreamCallbackResult.Continue;
        };

        try
        {
            _stream = new PortAudioSharp.Stream(
                inParams: parameters,
                outParams: null,
                sampleRate: AudioFormat.SampleRate,
                framesPerBuffer: (uint)settings.FrameSamples,
                streamFlags: StreamFlags.ClipOff,
                callback: callback,
                userData: IntPtr.Zero);

            _stream.Start();
        }
        catch (Exception ex)
        {
            _stream?.Dispose();
            _stream = null;
            _settings = null;
            throw new InvalidOperationException("Mic failed", ex);
        }
    }

    public bool TryDequeue(out byte[]? chunk)
    {
        return _queue.TryDequeue(out chunk);
    }

    public void Stop()
    {
        if (_stream == null)
        {
            return;
        }

        _stream.Stop();
        _stream.Dispose();
        _stream = null;
        _settings = null;
        Volatile.Write(ref _meterLevel, 0f);

        while (_queue.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

internal static class AdaptiveLatencySettingsExtensions
{
    public const double DefaultFrameDurationMs = 10;
}
