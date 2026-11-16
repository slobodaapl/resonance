using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Resonance.Scheduling;

namespace Resonance.Audio;

public interface IGameMixerPcmSink : IDisposable
{
    bool IsAvailable { get; }
    string Diagnostic { get; }
    void Play(GameMixerPcmStream stream);
    void Stop(GameMixerPcmStream stream);
}

public sealed class GameMixerPcmStream
{
    public const int SampleRate = 48_000;
    private readonly ConcurrentQueue<float[]> chunks = new();
    private float[]? current;
    private int position;
    private int completed;
    private int stopped;
    private long renderedSamples;
    private long producedSamples;
    private Exception? error;
    private float outputPeak;
    private long lastRender;
    internal readonly TaskCompletionSource Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long RenderedSamples => Interlocked.Read(ref renderedSamples);
    public bool IsDrained => Volatile.Read(ref completed) != 0 && RenderedSamples == Interlocked.Read(ref producedSamples);
    public Exception? Error => Volatile.Read(ref error);
    internal float Volume = 1f;
    public float OutputPeak => Volatile.Read(ref stopped) != 0
        || System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref lastRender)).TotalSeconds > .1
        ? 0 : Volatile.Read(ref outputPeak);

    internal void Write(float[] samples)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        Interlocked.Add(ref producedSamples, samples.Length);
        chunks.Enqueue(samples);
        Ready.TrySetResult();
    }

    internal void Complete(Exception? failure = null)
    {
        Volatile.Write(ref error, failure);
        Volatile.Write(ref completed, 1);
        Ready.TrySetResult();
    }

    internal void Stop()
    {
        Volatile.Write(ref stopped, 1);
        chunks.Clear();
    }

    public int Render(Span<float> interleaved, float gain)
    {
        interleaved.Clear();
        if (Volatile.Read(ref stopped) != 0) return 0;
        var frames = interleaved.Length / 8;
        var written = 0;
        var peak = 0f;
        while (written < frames)
        {
            if (current is null && !chunks.TryDequeue(out current)) break;
            var count = Math.Min(frames - written, current.Length - position);
            for (var index = 0; index < count; index++)
            {
                var sample = current[position + index] * gain * Volume * 0.70710678f;
                peak = Math.Max(peak, Math.Abs(sample));
                interleaved[(written + index) * 8] = sample;
                interleaved[(written + index) * 8 + 1] = sample;
            }
            position += count;
            written += count;
            if (position == current.Length) { current = null; position = 0; }
        }
        Interlocked.Add(ref renderedSamples, written);
        Volatile.Write(ref outputPeak, peak);
        Interlocked.Exchange(ref lastRender, System.Diagnostics.Stopwatch.GetTimestamp());
        return written;
    }
}

public sealed class StreamingGameMixerAudioBackend(
    IGameMixerPcmSink sink,
    Func<AudioProcessingOptions>? processingOptions = null) : IGameMixerAudioBackend
{
    private sealed class Preparation(DubLine line, CancellationToken lifetime) : IDisposable
    {
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(line.Token, lifetime);
        internal readonly GameMixerPcmStream Stream = new();
        internal Task Producer = Task.CompletedTask;
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            Stream.Stop();
            Cancellation.Cancel();
            _ = Producer.ContinueWith(_ => Cancellation.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private readonly object gate = new();
    private readonly Dictionary<DubLine, Preparation> prepared = [];
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? playback;
    private GameMixerPcmStream? playing;
    private int disposed;

    public bool IsAvailable => Volatile.Read(ref disposed) == 0 && sink.IsAvailable;
    public bool IsHealthy => IsAvailable;
    public string Diagnostic => sink.Diagnostic;
    public float? OutputPeak { get { lock (gate) return playing?.OutputPeak ?? 0; } }

    private Preparation GetPreparation(DubLine line)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            foreach (var stale in prepared.Where(pair => pair.Key.IsTerminal).ToArray())
            {
                stale.Value.Dispose();
                prepared.Remove(stale.Key);
            }
            if (prepared.TryGetValue(line, out var existing)) return existing;
            var processing = processingOptions?.Invoke() ?? AudioProcessingOptions.Default;
            var preparation = new Preparation(line, shutdown.Token);
            prepared.Add(line, preparation);
            preparation.Producer = Task.Run(() => Produce(line, preparation, processing));
            return preparation;
        }
    }

    public async Task PrepareAsync(DubLine line, float volume, CancellationToken token)
    {
        var preparation = GetPreparation(line);
        await preparation.Stream.Ready.Task.WaitAsync(token).ConfigureAwait(false);
        if (preparation.Stream.Error is { } error) throw error;
        line.PlaybackAssetReady = true;
    }

    private static void Produce(DubLine line, Preparation preparation, AudioProcessingOptions processing)
    {
        try
        {
            using var source = new ProducerSampleProvider(line.Audio, preparation.Cancellation.Token);
            ISampleProvider provider = source;
            if (line.ApplyBaseCloneCorrection)
            {
                if (processing.CorrectionFilter) provider = new BaseCloneCorrectionSampleProvider(provider);
                if (processing.LineNormalization) provider = new NormalizedSampleProvider(provider);
            }
            provider = new WdlResamplingSampleProvider(provider, GameMixerPcmStream.SampleRate);
            var buffer = new float[960];
            var total = 0;
            int read;
            while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            {
                total = checked(total + read);
                if (total > GameMixerPcmStream.SampleRate * (GameMixerPcmEncoder.MaxDurationSeconds + 1))
                    throw new InvalidDataException("GameMixer audio exceeds the bounded duration limit");
                for (var index = 0; index < read; index++)
                {
                    if (!float.IsFinite(buffer[index])) throw new InvalidDataException("Non-finite resampled GameMixer sample");
                    buffer[index] = Math.Clamp(buffer[index], -1f, 1f);
                }
                preparation.Stream.Write(buffer.AsSpan(0, read).ToArray());
            }
            preparation.Stream.Complete();
        }
        catch (Exception error) { preparation.Stream.Complete(error); }
    }

    public void Play(DubLine line, float volume, Action<DubLine> started,
        Action<DubLine> finished, Action<DubLine, Exception> failed)
    {
        if (line.State == DubLineState.Active) return;
        if (!IsAvailable || !float.IsFinite(volume))
        {
            if (line.TryTransition(DubLineState.Failed, DubLineState.Predicted, DubLineState.VoiceResolving,
                    DubLineState.Queued, DubLineState.Generating, DubLineState.Buffered))
            {
                failed(line, !float.IsFinite(volume) ? new ArgumentOutOfRangeException(nameof(volume))
                    : new InvalidOperationException(Diagnostic));
                line.Dispose();
            }
            return;
        }
        Stop(false);
        if (!line.TryTransition(DubLineState.Active, DubLineState.Predicted, DubLineState.VoiceResolving,
                DubLineState.Queued, DubLineState.Generating, DubLineState.Buffered)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(line.Token, shutdown.Token);
        lock (gate) playback = cancellation;
        _ = RunAsync();
        async Task RunAsync()
        {
            Preparation? preparation = null;
            try
            {
                preparation = GetPreparation(line);
                await PrepareAsync(line, volume, cancellation.Token).ConfigureAwait(false);
                lock (gate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    playing = preparation.Stream;
                    playing.Volume = Math.Clamp(volume, 0f, 2f);
                    sink.Play(playing);
                }
                var notified = false;
                var waiting = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (preparation.Stream.Error is { } error) throw error;
                    if (!sink.IsAvailable) throw new InvalidOperationException(sink.Diagnostic);
                    if (!notified && preparation.Stream.RenderedSamples > 0) { notified = true; started(line); }
                    if (preparation.Stream.IsDrained) break;
                    if (!notified && waiting.Elapsed > TimeSpan.FromSeconds(10))
                        throw new TimeoutException("FFXIV PCM mixer did not render within 10 seconds");
                    await Task.Delay(10, cancellation.Token).ConfigureAwait(false);
                }
                if (preparation.Stream.Error is { } terminalError) throw terminalError;
                if (!notified) throw new InvalidDataException("Speech producer completed without audio");
                await Task.Delay(FfxivGameMixerAudioBackend.DefaultOutputDrainGuard, cancellation.Token).ConfigureAwait(false);
                lock (gate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    sink.Stop(preparation.Stream);
                    if (line.TryTransition(DubLineState.Completed, DubLineState.Active)) finished(line);
                }
                line.Dispose();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!cancellation.IsCancellationRequested && line.TryTransition(DubLineState.Failed, DubLineState.Active))
                { failed(line, error); line.Dispose(); }
            }
            finally
            {
                lock (gate)
                {
                    if (preparation is not null)
                    {
                        sink.Stop(preparation.Stream);
                        prepared.Remove(line);
                        preparation.Dispose();
                    }
                    if (ReferenceEquals(playback, cancellation)) { playback = null; playing = null; }
                }
                cancellation.Dispose();
            }
        }
    }

    public void Stop(bool discardPrepared = true)
    {
        lock (gate)
        {
            playback?.Cancel();
            playback = null;
            if (playing is { } current) sink.Stop(current);
            playing = null;
            if (!discardPrepared) return;
            foreach (var (line, preparation) in prepared)
            {
                preparation.Dispose();
                line.Cancel();
            }
            prepared.Clear();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Stop();
        shutdown.Cancel();
        sink.Dispose();
        shutdown.Dispose();
    }

    private sealed class ProducerSampleProvider(StreamingAudioBuffer audio, CancellationToken token) : ISampleProvider, IDisposable
    {
        private AudioChunk? current;
        private int position;
        private int total;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(24_000, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            audio.MarkConsumerStarted();
            var written = 0;
            while (written < count)
            {
                if (current is null)
                {
                    if (!audio.Reader.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult()) break;
                    if (!audio.Reader.TryRead(out current)) continue;
                    position = 0;
                }
                var read = Math.Min(count - written, current.Count - position);
                current.Samples.Span.Slice(position, read).CopyTo(buffer.AsSpan(offset + written, read));
                total = checked(total + read);
                if (total > 24_000 * GameMixerPcmEncoder.MaxDurationSeconds)
                    throw new InvalidDataException("GameMixer producer exceeded the bounded duration limit");
                for (var index = offset + written; index < offset + written + read; index++)
                    if (!float.IsFinite(buffer[index])) throw new InvalidDataException("Non-finite GameMixer sample");
                position += read;
                written += read;
                if (position == current.Count) { current.Dispose(); current = null; }
            }
            return written;
        }
        public void Dispose() { current?.Dispose(); current = null; }
    }

    private sealed class NormalizedSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private float[]? prefix;
        private int prefixPosition;
        private float[] block = new float[480];
        private float[] next = new float[480];
        private int blockCount;
        private int blockPosition;
        private int nextCount;
        private double desiredGain = 1;
        private bool gainMeasured;
        private double previousGain = Double.NaN;
        private static readonly double Ceiling = Math.Pow(10, GameMixerPcmEncoder.PeakCeilingDbfs / 20);
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            if (prefix is null)
            {
                ReadPrefix();
                blockCount = ReadRaw(block);
                nextCount = ReadRaw(next);
                LimitBlock();
            }
            var written = 0;
            while (written < count && blockCount > 0)
            {
                var copied = Math.Min(count - written, blockCount - blockPosition);
                block.AsSpan(blockPosition, copied).CopyTo(buffer.AsSpan(offset + written, copied));
                written += copied;
                blockPosition += copied;
                if (blockPosition != blockCount) continue;
                (block, next) = (next, block);
                blockCount = nextCount;
                blockPosition = 0;
                nextCount = blockCount == 0 ? 0 : ReadRaw(next);
                LimitBlock();
            }
            return written;
        }

        private int ReadRaw(float[] target)
        {
            if (!gainMeasured && prefixPosition == prefix!.Length) ReadPrefix();
            var copied = Math.Min(target.Length, prefix!.Length - prefixPosition);
            prefix.AsSpan(prefixPosition, copied).CopyTo(target);
            prefixPosition += copied;
            var read = copied == target.Length ? 0 : source.Read(target, copied, target.Length - copied);
            for (var index = 0; index < copied + read; index++)
                if (!float.IsFinite(target[index])) throw new InvalidDataException("Non-finite corrected GameMixer sample");
            return copied + read;
        }

        private void ReadPrefix()
        {
            prefix = new float[5760];
            var read = source.Read(prefix, 0, prefix.Length);
            Array.Resize(ref prefix, read);
            prefixPosition = 0;
            // Leading silence must not fix the entire line's gain at unity.
            if (GameMixerPcmEncoder.MeasureBaseCloneGain(prefix) is not { } gainDb) return;
            desiredGain = Math.Pow(10, gainDb / 20);
            gainMeasured = true;
        }

        private void LimitBlock()
        {
            var peak = 0d;
            for (var index = 0; index < blockCount; index++) peak = Math.Max(peak, Math.Abs(block[index]));
            for (var index = 0; index < nextCount; index++) peak = Math.Max(peak, Math.Abs(next[index]));
            var target = peak == 0 ? desiredGain : Math.Min(desiredGain, Ceiling / peak);
            if (Double.IsNaN(previousGain)) previousGain = target;
            var end = Math.Min(target, previousGain + (desiredGain - previousGain) * 0.2);
            // The preceding block already saw this block's peak; both ramp endpoints are safe.
            for (var index = 0; index < blockCount; index++)
                block[index] *= (float)(previousGain + (end - previousGain) * (index + 1) / blockCount);
            previousGain = end;
        }
    }
}
