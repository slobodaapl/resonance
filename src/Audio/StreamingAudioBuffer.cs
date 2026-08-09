using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;

namespace Resonance.Audio;

public sealed class AudioChunk : IDisposable
{
    private float[]? samples;
    public int Count { get; }
    public ReadOnlyMemory<float> Samples => samples?.AsMemory(0, Count) ?? ReadOnlyMemory<float>.Empty;

    internal AudioChunk(float[] samples, int count)
    {
        this.samples = samples;
        Count = count;
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref samples, null);
        if (value is not null) ArrayPool<float>.Shared.Return(value);
    }
}

public sealed class StreamingAudioBuffer : IDisposable
{
    private readonly Channel<AudioChunk> chunks = Channel.CreateUnbounded<AudioChunk>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private int completed;
    private long totalSamplesWritten;
    private long totalSamplesConsumed;
    private long firstWriteTimestamp;
    private int consumerStarted;
    private StreamingAudioBuffer? capture;

    public bool ProducerCompleted => Volatile.Read(ref completed) != 0;
    public ChannelReader<AudioChunk> Reader => chunks.Reader;
    public long TotalSamplesWritten => Interlocked.Read(ref totalSamplesWritten);
    public long BufferedSamples => Math.Max(0,
        Interlocked.Read(ref totalSamplesWritten) - Interlocked.Read(ref totalSamplesConsumed));
    public long FirstWriteTimestamp => Interlocked.Read(ref firstWriteTimestamp);
    public bool ConsumerStarted => Volatile.Read(ref consumerStarted) != 0;

    // Native callback path: bounded work only—rent, copy, enqueue, return.
    public bool TryWrite(ReadOnlySpan<float> samples)
    {
        if (ProducerCompleted || samples.IsEmpty) return false;
        var rented = ArrayPool<float>.Shared.Rent(samples.Length);
        samples.CopyTo(rented);
        var chunk = new AudioChunk(rented, samples.Length);
        if (chunks.Writer.TryWrite(chunk))
        {
            Interlocked.CompareExchange(ref firstWriteTimestamp, Stopwatch.GetTimestamp(), 0);
            Interlocked.Add(ref totalSamplesWritten, samples.Length);
            Volatile.Read(ref capture)?.TryWrite(samples);
            return true;
        }
        chunk.Dispose();
        return false;
    }

    public void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        chunks.Writer.TryComplete(error);
        Volatile.Read(ref capture)?.Complete(error);
    }

    public StreamingAudioBuffer CreateCapture()
    {
        var created = new StreamingAudioBuffer();
        if (Interlocked.CompareExchange(ref capture, created, null) is null) return created;
        created.Dispose();
        throw new InvalidOperationException("An audio capture is already attached");
    }

    public static StreamingAudioBuffer FromSamples(ReadOnlySpan<float> samples, int chunkSize = 4096)
    {
        var result = new StreamingAudioBuffer();
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
        while (chunks.Reader.TryRead(out var chunk)) chunk.Dispose();
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
        while (chunks.Reader.TryRead(out var chunk)) chunk.Dispose();
    }
}
