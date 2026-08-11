using System.Diagnostics;
using Resonance.Audio;
using Resonance.Data;
using Resonance.Tts;

namespace Resonance.Scheduling;

public readonly record struct VoiceResolution(VoiceReference? Reference, bool Deferred)
{
    public static VoiceResolution Ready(VoiceReference? reference) => new(reference, false);
    public static VoiceResolution DeferredPrediction => new(null, true);
}

public sealed class DubScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly PriorityQueue<DubLine, (int Tier, double Slack, long Sequence)> queue = new();
    private readonly SemaphoreSlim ready = new(0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ITtsRuntime runtime;
    private readonly Func<DubLine, CancellationToken, ValueTask<VoiceResolution>> resolveVoice;
    private readonly LineCache lineCache;
    private readonly string modelHash;
    private readonly string defaultLanguage;
    private readonly Func<long> cacheCaptureLimitBytes;
    private readonly object disposeGate = new();
    private readonly Task worker;
    private DubLine? generating;
    private int disposed;
    private Task? disposeTask;
    private double meanTimeToFirstAudioSeconds = 0.35;
    private double meanRealTimeFactor = 0.8;
    private double meanCharactersPerSecond = 14;
    private bool hasRuntimeMeasurement;

    public event Action<DubLine>? LineBuffered;
    public event Action<DubLine>? PredictionStreamable;
    public event Action<DubLine, Exception>? LineFailed;
    public event Action? BecameIdle;
    public bool HasUrgentWork
    {
        get
        {
            lock (gate)
                return generating is { ActualStatus: ActualStatus.Actual, IsTerminal: false }
                    || queue.UnorderedItems.Any(item => item.Element.ActualStatus == ActualStatus.Actual && !item.Element.IsTerminal);
        }
    }

    public DubScheduler(ITtsRuntime runtime, Func<DubLine, CancellationToken, ValueTask<VoiceResolution>> resolveVoice,
        LineCache lineCache, string modelHash, string language, Func<long>? cacheCaptureLimitBytes = null)
    {
        this.runtime = runtime;
        this.resolveVoice = resolveVoice;
        this.lineCache = lineCache;
        this.modelHash = modelHash;
        defaultLanguage = language;
        this.cacheCaptureLimitBytes = cacheCaptureLimitBytes ?? (() => 64L * 1024 * 1024);
        worker = Task.Run(WorkerAsync);
    }

    public void Enqueue(DubLine line)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (!line.TryTransition(DubLineState.Queued, DubLineState.Predicted,
                    DubLineState.VoiceResolving, DubLineState.Queued, DubLineState.Buffered)) return;
            line.Language ??= defaultLanguage;
            Estimate(line, DateTimeOffset.UtcNow);
            queue.Enqueue(line, Priority(line, DateTimeOffset.UtcNow));
            try { ready.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void PromoteActual(long sessionEpoch, long sequence, DateTimeOffset deadline)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            foreach (var item in queue.UnorderedItems)
            {
                var line = item.Element;
                if (line.SessionEpoch != sessionEpoch || line.Sequence != sequence) continue;
                line.ActualStatus = ActualStatus.Actual;
                line.PlaybackDeadline = deadline;
            }
            RebuildQueue(DateTimeOffset.UtcNow);
            try { ready.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    public void InvalidateEpoch(long epoch)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            foreach (var item in queue.UnorderedItems)
                if (item.Element.SessionEpoch == epoch) item.Element.Cancel(DubLineState.Invalidated);
            if (generating?.SessionEpoch == epoch) generating.Cancel(DubLineState.Invalidated);
            RebuildQueue(DateTimeOffset.UtcNow);
        }
    }

    public void NativeVoiceStarted(long epoch, long actualSequence)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            foreach (var item in queue.UnorderedItems)
            {
                var line = item.Element;
                if (line.SessionEpoch == epoch && line.Sequence == actualSequence)
                    line.TryMarkNativeVoiced(
                        DubLineState.Predicted,
                        DubLineState.VoiceResolving,
                        DubLineState.Queued,
                        DubLineState.Generating,
                        DubLineState.Buffered,
                        DubLineState.Active);
            }
            if (generating is { } active && active.SessionEpoch == epoch && active.Sequence == actualSequence)
                active.TryMarkNativeVoiced(
                    DubLineState.Predicted,
                    DubLineState.VoiceResolving,
                    DubLineState.Queued,
                    DubLineState.Generating,
                    DubLineState.Buffered,
                    DubLineState.Active);
            RebuildQueue(DateTimeOffset.UtcNow);
        }
    }

    private async Task WorkerAsync()
    {
        while (!shutdown.IsCancellationRequested)
        {
            try { await ready.WaitAsync(shutdown.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            DubLine? line;
            lock (gate)
            {
                do
                {
                    line = queue.TryDequeue(out var candidate, out _) ? candidate : null;
                } while (line is not null && line.IsTerminal);
                generating = line;
            }
            if (line is null) continue;

            try
            {
                var operationStarted = Stopwatch.GetTimestamp();
                var language = line.Language ?? defaultLanguage;
                if (!line.TryTransition(DubLineState.VoiceResolving, DubLineState.Queued)) continue;
                var resolution = resolveVoice(line, line.Token).AsTask();
                var playbackAuthorized = await AuthorizeStreamingAsync(line, resolution, false).ConfigureAwait(false);
                var voiceResolution = await resolution.ConfigureAwait(false);
                // Resolution can finish after native playback, session
                // invalidation, or scheduler shutdown.  Never replace audio or
                // advance state for a line that became terminal while the
                // deferred prediction was being resolved.
                line.Token.ThrowIfCancellationRequested();
                if (line.IsTerminal) continue;
                if (voiceResolution.Deferred)
                {
                    line.TryReplaceAudioAndTransition(
                        new StreamingAudioBuffer(),
                        DubLineState.Predicted,
                        DubLineState.VoiceResolving);
                    continue;
                }
                var voice = voiceResolution.Reference;
                var seed = StableSeed($"{line.SpeakerKey}\0{language}");
                if (line.DirectSynthesisCompleted)
                {
                    RecordMeasurement(line, operationStarted);
                    if (!playbackAuthorized && line.TryTransition(
                            DubLineState.Buffered,
                            DubLineState.VoiceResolving,
                            DubLineState.Queued,
                            DubLineState.Predicted,
                            DubLineState.Generating))
                    {
                        LineBuffered?.Invoke(line);
                    }
                    continue;
                }
                if (line.VoiceProfileId is not null && line.VoiceProfileHash is not null
                    && await lineCache.TryPopulateAsync(line, line.VoiceProfileHash, modelHash, language, seed, line.Token).ConfigureAwait(false))
                {
                    LineBuffered?.Invoke(line);
                    continue;
                }
                if (!line.TryTransition(
                        DubLineState.Generating,
                        DubLineState.VoiceResolving,
                        DubLineState.Queued,
                        DubLineState.Predicted,
                        DubLineState.Buffered)) continue;
                operationStarted = Stopwatch.GetTimestamp();
                // Only persistent profile synthesis is cacheable.  Transient
                // predictions and direct first-line VoiceDesign streams must
                // never attach a second unconsumed PCM channel merely for a
                // cache path that cannot be used.
                var captureLimitBytes = cacheCaptureLimitBytes();
                using var capture = line.VoiceProfileId is not null && line.VoiceProfileHash is not null
                    && captureLimitBytes > 0
                    ? line.Audio.CreateCapture(captureLimitBytes)
                    : null;
                var synthesis = runtime.SynthesizeAsync(
                    new(line.Text, language, voice, null, seed),
                    line.Audio,
                    line.Token);
                playbackAuthorized = await AuthorizeStreamingAsync(line, synthesis, playbackAuthorized).ConfigureAwait(false);
                await synthesis.ConfigureAwait(false);
                RecordMeasurement(line, operationStarted);
                var capturedSamples = capture is null || capture.Overflowed
                    ? null
                    : await capture.DrainAsync(line.Token).ConfigureAwait(false);
                if (!line.IsTerminal)
                {
                    if (capturedSamples is not null && line.VoiceProfileId is not null
                        && line.VoiceProfileHash is not null)
                        await lineCache.StoreAsync(line, line.VoiceProfileId, line.VoiceProfileHash,
                            modelHash, language, seed, capturedSamples, line.Token).ConfigureAwait(false);
                    if (!playbackAuthorized && line.TryTransition(
                            DubLineState.Buffered,
                            DubLineState.Generating))
                    {
                        LineBuffered?.Invoke(line);
                    }
                }
            }
            catch (OperationCanceledException) when (line.Token.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (line.TryTransition(
                        DubLineState.Failed,
                        DubLineState.VoiceResolving,
                        DubLineState.Generating,
                        DubLineState.Queued,
                        DubLineState.Buffered,
                        DubLineState.Predicted))
                {
                    line.Audio.Complete(error);
                    LineFailed?.Invoke(line, error);
                }
            }
            finally
            {
                var idle = false;
                lock (gate)
                {
                    if (ReferenceEquals(generating, line)) generating = null;
                    idle = generating is null
                        && !queue.UnorderedItems.Any(item => item.Element.ActualStatus == ActualStatus.Actual
                            && !item.Element.IsTerminal);
                }
                if (idle && Volatile.Read(ref disposed) == 0) BecameIdle?.Invoke();
            }
        }
    }

    private static (int, double, long) Priority(DubLine line, DateTimeOffset now)
    {
        var tier = line.ActualStatus == ActualStatus.Actual ? 0 : 1;
        return (tier, line.SlackSeconds(now), line.Sequence);
    }

    private void Estimate(DubLine line, DateTimeOffset now)
    {
        line.PredictedAudioDurationSeconds = Math.Max(0.5, line.Text.Length / meanCharactersPerSecond);
        line.EstimatedGenerationSeconds = meanTimeToFirstAudioSeconds
                                          + line.PredictedAudioDurationSeconds * meanRealTimeFactor;
        if (line.ActualStatus == ActualStatus.Actual)
        {
            line.PlaybackDeadline = now;
            return;
        }

        var priorAudio = queue.UnorderedItems
            .Select(item => item.Element)
            .Where(candidate => candidate.ActualStatus == ActualStatus.Predicted
                                && candidate.Sequence < line.Sequence && !candidate.IsTerminal)
            .Sum(candidate => candidate.PredictedAudioDurationSeconds);
        if (generating is { ActualStatus: ActualStatus.Predicted } active && active.Sequence < line.Sequence)
            priorAudio += active.PredictedAudioDurationSeconds;
        line.PlaybackDeadline = now + TimeSpan.FromSeconds(priorAudio);
    }

    private void RecordMeasurement(DubLine line, long started)
    {
        var sampleCount = line.Audio.TotalSamplesWritten;
        var firstWrite = line.Audio.FirstWriteTimestamp;
        if (sampleCount <= 0 || firstWrite < started) return;
        var duration = sampleCount / 24000d;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var ttfa = Stopwatch.GetElapsedTime(started, firstWrite).TotalSeconds;
        var rtf = elapsed / duration;
        var charactersPerSecond = line.Text.Length / duration;
        lock (gate)
        {
            meanTimeToFirstAudioSeconds = Ewma(meanTimeToFirstAudioSeconds, ttfa);
            meanRealTimeFactor = Ewma(meanRealTimeFactor, rtf);
            hasRuntimeMeasurement = true;
            if (charactersPerSecond > 0) meanCharactersPerSecond = Ewma(meanCharactersPerSecond, charactersPerSecond);
        }
    }

    private static double Ewma(double current, double sample) => current * 0.8 + sample * 0.2;

    private async Task<bool> AuthorizeStreamingAsync(DubLine line, Task phase, bool alreadyAuthorized)
    {
        if (alreadyAuthorized) return true;
        while (!line.IsTerminal)
        {
            var bufferedSeconds = line.Audio.BufferedSamples / 24000d;
            double rtf;
            bool measured;
            lock (gate)
            {
                rtf = meanRealTimeFactor;
                measured = hasRuntimeMeasurement;
            }
            var remainingAudioSeconds = Math.Max(0, line.PredictedAudioDurationSeconds - bufferedSeconds);
            var remainingGenerationSeconds = remainingAudioSeconds * rtf;
            var canStart = StreamingStartPolicy.ShouldStart(
                    phase.IsCompleted || line.Audio.ProducerCompleted,
                    bufferedSeconds,
                    remainingGenerationSeconds,
                    measured ? rtf : Double.PositiveInfinity,
                    (DateTimeOffset.UtcNow - line.PlaybackDeadline).TotalSeconds);
            if (canStart && line.ActualStatus == ActualStatus.Predicted)
            {
                if (!line.CanStartStreaming)
                {
                    line.CanStartStreaming = true;
                    PredictionStreamable?.Invoke(line);
                }
            }
            else if (canStart && line.ActualStatus == ActualStatus.Actual)
            {
                if (line.TryTransition(
                        DubLineState.Buffered,
                        DubLineState.VoiceResolving,
                        DubLineState.Generating,
                        DubLineState.Queued,
                        DubLineState.Predicted))
                {
                    LineBuffered?.Invoke(line);
                    return true;
                }
                return false;
            }
            if (phase.IsCompleted) return false;
            await Task.Delay(10, line.Token).ConfigureAwait(false);
        }
        return false;
    }

    private void RebuildQueue(DateTimeOffset now)
    {
        var retained = queue.UnorderedItems.Select(item => item.Element).Where(line => !line.IsTerminal).ToArray();
        queue.Clear();
        foreach (var line in retained) queue.Enqueue(line, Priority(line, now));
    }

    private static long StableSeed(string value)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var character in value) { hash ^= character; hash *= prime; }
        return unchecked((long)hash);
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            disposeTask = DisposeCoreAsync().AsTask();
            return new ValueTask(disposeTask);
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            generating?.Cancel();
            foreach (var item in queue.UnorderedItems) item.Element.Cancel();
            queue.Clear();
            try { ready.Release(); }
            catch (ObjectDisposedException) { }
        }
        shutdown.Cancel();
        try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        ready.Dispose();
        shutdown.Dispose();
    }
}
