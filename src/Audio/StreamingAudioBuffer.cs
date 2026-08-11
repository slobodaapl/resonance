using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

namespace Resonance.Audio;

public sealed class AudioChunk : IDisposable
{
    private float[]? samples;
    private Action<int>? released;
    public int Count { get; }
    public ReadOnlyMemory<float> Samples => samples?.AsMemory(0, Count) ?? ReadOnlyMemory<float>.Empty;

    internal AudioChunk(float[] samples, int count, Action<int>? released = null)
    {
        this.samples = samples;
        Count = count;
        this.released = released;
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref samples, null);
        if (value is not null) ArrayPool<float>.Shared.Return(value);
        var release = Interlocked.Exchange(ref released, null);
        release?.Invoke(Count);
    }
}

public sealed class StreamingAudioBuffer : IDisposable
{
    private const int SampleRate = 24_000;
    private const long DefaultMaxBufferedBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan DefaultMaxBufferedSeconds = TimeSpan.FromSeconds(30);
    private readonly Channel<AudioChunk> chunks = Channel.CreateUnbounded<AudioChunk>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly object capacityGate = new();
    private readonly long maxBufferedSamples;
    private TaskCompletionSource? capacityChanged;
    private long reservedSamples;
    private int completed;
    private long totalSamplesWritten;
    private long totalSamplesConsumed;
    private long firstWriteTimestamp;
    private int consumerStarted;
    private int overflowed;
    private StreamingAudioBuffer? capture;

    public StreamingAudioBuffer(
        double maxBufferedSeconds = 30,
        long maxBufferedBytes = DefaultMaxBufferedBytes)
    {
        if (!double.IsFinite(maxBufferedSeconds) || maxBufferedSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBufferedSeconds));
        if (maxBufferedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
        var secondsLimit = checked((long)Math.Ceiling(maxBufferedSeconds * SampleRate));
        var byteLimit = Math.Max(1, maxBufferedBytes / sizeof(float));
        maxBufferedSamples = Math.Max(1, Math.Min(secondsLimit, byteLimit));
    }

    public bool ProducerCompleted => Volatile.Read(ref completed) != 0;
    public ChannelReader<AudioChunk> Reader => chunks.Reader;
    public long TotalSamplesWritten => Interlocked.Read(ref totalSamplesWritten);
    public long BufferedSamples => Math.Max(0,
        Interlocked.Read(ref totalSamplesWritten) - Interlocked.Read(ref totalSamplesConsumed));
    public long FirstWriteTimestamp => Interlocked.Read(ref firstWriteTimestamp);
    public bool ConsumerStarted => Volatile.Read(ref consumerStarted) != 0;
    public bool Overflowed => Volatile.Read(ref overflowed) != 0;
    public long MaxBufferedSamples => maxBufferedSamples;

    // Native callback path: bounded work only—rent, copy, enqueue, return.
    public bool TryWrite(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return false;
        if (!TryReserve(samples.Length))
        {
            MarkCaptureOverflow();
            return false;
        }
        var rented = ArrayPool<float>.Shared.Rent(samples.Length);
        samples.CopyTo(rented);
        var chunk = new AudioChunk(rented, samples.Length, ReleaseReserved);
        if (chunks.Writer.TryWrite(chunk))
        {
            Interlocked.CompareExchange(ref firstWriteTimestamp, Stopwatch.GetTimestamp(), 0);
            Interlocked.Add(ref totalSamplesWritten, samples.Length);
            var captureBuffer = Volatile.Read(ref capture);
            if (captureBuffer is not null && !captureBuffer.TryWrite(samples))
                Volatile.Write(ref captureBuffer.overflowed, 1);
            return true;
        }
        chunk.Dispose();
        MarkCaptureOverflow();
        return false;
    }

    // Managed producers can await capacity instead of dropping a native-sized
    // chunk when playback is temporarily slower than inference.
    public async ValueTask<bool> WriteAsync(ReadOnlyMemory<float> samples, CancellationToken token = default)
    {
        if (samples.IsEmpty) return true;
        bool reserved;
        try { reserved = await ReserveAsync(samples.Length, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            MarkCaptureOverflow();
            throw;
        }
        if (!reserved)
        {
            MarkCaptureOverflow();
            return false;
        }
        var rented = ArrayPool<float>.Shared.Rent(samples.Length);
        samples.Span.CopyTo(rented);
        var chunk = new AudioChunk(rented, samples.Length, ReleaseReserved);
        if (!chunks.Writer.TryWrite(chunk))
        {
            chunk.Dispose();
            MarkCaptureOverflow();
            return false;
        }
        Interlocked.CompareExchange(ref firstWriteTimestamp, Stopwatch.GetTimestamp(), 0);
        Interlocked.Add(ref totalSamplesWritten, samples.Length);
        var captureBuffer = Volatile.Read(ref capture);
        if (captureBuffer is not null && !captureBuffer.TryWrite(samples.Span))
            Volatile.Write(ref captureBuffer.overflowed, 1);
        return true;
    }

    public void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        chunks.Writer.TryComplete(error);
        SignalCapacityWaiters();
        Volatile.Read(ref capture)?.Complete(error);
    }

    public void Invalidate(Exception? error = null)
    {
        Complete(error ?? new OperationCanceledException("The audio stream was invalidated."));
        DiscardBuffered();
    }

    public StreamingAudioBuffer CreateCapture()
        => CreateCapture(DefaultMaxBufferedBytes);

    public StreamingAudioBuffer CreateCapture(long maxBufferedBytes)
    {
        if (maxBufferedBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
        var created = new StreamingAudioBuffer(
            Math.Max(DefaultMaxBufferedSeconds.TotalSeconds,
                maxBufferedBytes / (double)(SampleRate * sizeof(float)) + 1),
            maxBufferedBytes);
        if (Interlocked.CompareExchange(ref capture, created, null) is null) return created;
        created.Dispose();
        throw new InvalidOperationException("An audio capture is already attached");
    }

    public static StreamingAudioBuffer FromSamples(ReadOnlySpan<float> samples, int chunkSize = 4096)
    {
        var result = new StreamingAudioBuffer(
            Math.Max(DefaultMaxBufferedSeconds.TotalSeconds, samples.Length / (double)SampleRate + 1),
            Math.Max(DefaultMaxBufferedBytes, checked((long)samples.Length * sizeof(float))));
        for (var offset = 0; offset < samples.Length; offset += chunkSize)
            result.TryWrite(samples.Slice(offset, Math.Min(chunkSize, samples.Length - offset)));
        result.Complete();
        return result;
    }

    public async Task<float[]> DrainAsync(CancellationToken token)
    {
        var result = new List<float>();
        await foreach (var chunk in Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            result.AddRange(chunk.Samples.Span);
            chunk.Dispose();
        }
        return result.ToArray();
    }

    public void DiscardBuffered()
    {
        var dropped = false;
        while (chunks.Reader.TryRead(out var chunk))
        {
            dropped = true;
            chunk.Dispose();
        }
        if (dropped) MarkCaptureOverflow();
        Interlocked.Exchange(ref totalSamplesWritten, 0);
        Interlocked.Exchange(ref totalSamplesConsumed, 0);
        Interlocked.Exchange(ref firstWriteTimestamp, 0);
        Volatile.Read(ref capture)?.DiscardBuffered();
    }

    public void MarkConsumerStarted() => Interlocked.Exchange(ref consumerStarted, 1);

    public void ReportConsumed(int sampleCount)
    {
        if (sampleCount > 0) Interlocked.Add(ref totalSamplesConsumed, sampleCount);
    }

    public void Dispose()
    {
        Complete();
        DiscardBuffered();
    }

    private bool TryReserve(int sampleCount)
    {
        lock (capacityGate)
        {
            if (Volatile.Read(ref completed) != 0 || sampleCount > maxBufferedSamples
                || reservedSamples > maxBufferedSamples - sampleCount) return false;
            reservedSamples += sampleCount;
            return true;
        }
    }

    private async ValueTask<bool> ReserveAsync(int sampleCount, CancellationToken token)
    {
        if (sampleCount > maxBufferedSamples) return false;
        while (true)
        {
            Task wait;
            lock (capacityGate)
            {
                if (Volatile.Read(ref completed) != 0) return false;
                if (reservedSamples <= maxBufferedSamples - sampleCount)
                {
                    reservedSamples += sampleCount;
                    return true;
                }
                capacityChanged ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                wait = capacityChanged.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private void ReleaseReserved(int sampleCount)
    {
        lock (capacityGate)
        {
            reservedSamples = Math.Max(0, reservedSamples - sampleCount);
            SignalCapacityWaitersLocked();
        }
    }

    private void SignalCapacityWaiters()
    {
        lock (capacityGate) SignalCapacityWaitersLocked();
    }

    private void SignalCapacityWaitersLocked()
    {
        var signal = capacityChanged;
        capacityChanged = null;
        signal?.TrySetResult();
    }

    private void MarkCaptureOverflow()
    {
        var captureBuffer = Volatile.Read(ref capture);
        if (captureBuffer is not null) Volatile.Write(ref captureBuffer.overflowed, 1);
    }
}
