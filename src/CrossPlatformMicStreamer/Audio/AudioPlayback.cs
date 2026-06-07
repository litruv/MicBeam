using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PortAudioSharp;

namespace CrossPlatformMicStreamer.Audio;

public sealed class AudioPlayback : IDisposable
{
    private readonly ConcurrentQueue<byte[]> _queue = new();
    private readonly object _currentChunkLock = new();
    private byte[]? _currentChunk;
    private int _currentOffset;
    private int _underrunCount;
    private int _saturatedTicks;
    private float _meterLevel;
    private AdaptiveLatencySettings? _settings;
    private PortAudioSharp.Stream? _stream;

    public float MeterLevel => Volatile.Read(ref _meterLevel);

    public double QueuedDurationMs
    {
        get
        {
            if (_settings == null)
            {
                return 0;
            }

            var bytesPerFrame = AudioFormat.FrameBytes(1, _settings.BitDepth);
            var queuedSamples = _queue.Sum(chunk => chunk.Length / bytesPerFrame);

            lock (_currentChunkLock)
            {
                if (_currentChunk != null)
                {
                    queuedSamples += (_currentChunk.Length - _currentOffset) / bytesPerFrame;
                }
            }

            return queuedSamples * 1000.0 / AudioFormat.SampleRate;
        }
    }

    public int ConsumeUnderrunCount()
    {
        return Interlocked.Exchange(ref _underrunCount, 0);
    }

    public int PeekUnderrunCount() => Volatile.Read(ref _underrunCount);

    public int ConsumeSaturatedTicks()
    {
        return Interlocked.Exchange(ref _saturatedTicks, 0);
    }

    public int PeekSaturatedTicks() => Volatile.Read(ref _saturatedTicks);

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
            suggestedLatency = Math.Min(info.defaultLowOutputLatency, settings.TargetLatencySeconds),
            hostApiSpecificStreamInfo = IntPtr.Zero,
        };

        PortAudioSharp.Stream.Callback callback = (IntPtr _, IntPtr output, uint frameCount,
            ref StreamCallbackTimeInfo __, StreamCallbackFlags ___, IntPtr ____) =>
        {
            if (output == IntPtr.Zero)
            {
                return StreamCallbackResult.Continue;
            }

            var byteCount = (int)frameCount * bytesPerFrame;
            var buffer = new byte[byteCount];

            FillBuffer(buffer);

            Marshal.Copy(buffer, 0, output, byteCount);
            return StreamCallbackResult.Continue;
        };

        try
        {
            _stream = new PortAudioSharp.Stream(
                inParams: null,
                outParams: parameters,
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
            throw new InvalidOperationException("Output failed", ex);
        }
    }

    public void Enqueue(byte[] chunk)
    {
        if (_settings == null)
        {
            return;
        }

        if (_queue.Count >= _settings.MaxQueuedFrames)
        {
            Interlocked.Increment(ref _saturatedTicks);
            _queue.TryDequeue(out byte[]? _);
        }

        _queue.Enqueue(chunk);
        Volatile.Write(
            ref _meterLevel,
            AudioLevel.UpdatePeak(
                Volatile.Read(ref _meterLevel),
                AudioLevel.PeakFromBuffer(chunk, _settings.BitDepth)));
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

        lock (_currentChunkLock)
        {
            _currentChunk = null;
            _currentOffset = 0;
        }

        while (_queue.TryDequeue(out _))
        {
        }
    }

    private void FillBuffer(byte[] buffer)
    {
        var written = 0;

        while (written < buffer.Length)
        {
            byte[]? chunk;
            var offset = 0;

            lock (_currentChunkLock)
            {
                if (_currentChunk == null || _currentOffset >= _currentChunk.Length)
                {
                    if (!_queue.TryDequeue(out _currentChunk))
                    {
                        _currentChunk = null;
                        break;
                    }

                    _currentOffset = 0;
                }

                chunk = _currentChunk;
                offset = _currentOffset;
            }

            if (chunk == null)
            {
                break;
            }

            var available = chunk.Length - offset;
            var toCopy = Math.Min(available, buffer.Length - written);
            Buffer.BlockCopy(chunk, offset, buffer, written, toCopy);
            written += toCopy;

            lock (_currentChunkLock)
            {
                _currentOffset += toCopy;
            }
        }

        if (written < buffer.Length)
        {
            Interlocked.Increment(ref _underrunCount);
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
