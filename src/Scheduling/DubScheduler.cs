using System.Diagnostics;
using Resonance.Data;
using Resonance.Tts;

namespace Resonance.Scheduling;

public sealed class DubScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly PriorityQueue<DubLine, (int Tier, double Slack, long Sequence)> queue = new();
    private readonly SemaphoreSlim ready = new(0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ITtsRuntime runtime;
    private readonly Func<DubLine, CancellationToken, ValueTask<VoiceReference?>> resolveVoice;
    private readonly LineCache lineCache;
    private readonly string modelHash;
    private readonly string defaultLanguage;
    private readonly Task worker;
    private DubLine? generating;
    private double meanTimeToFirstAudioSeconds = 0.35;
    private double meanRealTimeFactor = 0.8;
    private double meanCharactersPerSecond = 14;

    public event Action<DubLine>? LineBuffered;
    public event Action<DubLine, Exception>? LineFailed;
    public bool HasUrgentWork
    {
        get
        {
            lock (gate)
                return generating?.ActualStatus == ActualStatus.Actual
                    || queue.UnorderedItems.Any(item => item.Element.ActualStatus == ActualStatus.Actual && !item.Element.IsTerminal);
        }
    }

    public DubScheduler(ITtsRuntime runtime, Func<DubLine, CancellationToken, ValueTask<VoiceReference?>> resolveVoice,
        LineCache lineCache, string modelHash, string language)
    {
        this.runtime = runtime;
        this.resolveVoice = resolveVoice;
        this.lineCache = lineCache;
        this.modelHash = modelHash;
        defaultLanguage = language;
        worker = Task.Run(WorkerAsync);
    }

    public void Enqueue(DubLine line)
    {
        lock (gate)
        {
            if (line.IsTerminal) return;
            line.Language ??= defaultLanguage;
            Estimate(line, DateTimeOffset.UtcNow);
            line.State = DubLineState.Queued;
            queue.Enqueue(line, Priority(line, DateTimeOffset.UtcNow));
        }
        ready.Release();
    }

    public void PromoteActual(long sessionEpoch, long sequence, DateTimeOffset deadline)
    {
        lock (gate)
        {
            foreach (var item in queue.UnorderedItems)
            {
                var line = item.Element;
                if (line.SessionEpoch != sessionEpoch || line.Sequence != sequence) continue;
                line.ActualStatus = ActualStatus.Actual;
                line.PlaybackDeadline = deadline;
            }
            RebuildQueue(DateTimeOffset.UtcNow);
        }
        ready.Release();
    }

    public void InvalidateEpoch(long epoch)
    {
        lock (gate)
        {
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
            foreach (var item in queue.UnorderedItems)
            {
                var line = item.Element;
                if (line.SessionEpoch == epoch && line.Sequence == actualSequence)
                {
                    line.NativeVoiceStatus = NativeVoiceStatus.NativeVoiced;
                    line.Cancel(DubLineState.NativeVoiced);
                }
            }
            if (generating is { } active && active.SessionEpoch == epoch && active.Sequence == actualSequence)
            {
                active.NativeVoiceStatus = NativeVoiceStatus.NativeVoiced;
                active.Cancel(DubLineState.NativeVoiced);
            }
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
                using var capture = line.Audio.CreateCapture();
                line.State = DubLineState.VoiceResolving;
                var resolution = resolveVoice(line, line.Token).AsTask();
                var playbackAuthorized = await AuthorizeStreamingAsync(line, resolution, false).ConfigureAwait(false);
                var voice = await resolution.ConfigureAwait(false);
                var seed = StableSeed($"{line.SpeakerKey}\0{language}");
                if (line.DirectSynthesisCompleted)
                {
                    RecordMeasurement(line, operationStarted);
                    var directSamples = await capture.DrainAsync(line.Token).ConfigureAwait(false);
                    if (line.VoiceProfileId is not null && line.VoiceProfileHash is not null)
                        await lineCache.StoreAsync(line, line.VoiceProfileId, line.VoiceProfileHash,
                            modelHash, language, seed, directSamples, line.Token).ConfigureAwait(false);
                    if (!line.IsTerminal && !playbackAuthorized)
                    {
                        line.State = DubLineState.Buffered;
                        LineBuffered?.Invoke(line);
                    }
                    continue;
                }
                if (line.VoiceProfileId is not null && line.VoiceProfileHash is not null
                    && await lineCache.TryPopulateAsync(line, line.VoiceProfileHash, modelHash, language, seed, line.Token).ConfigureAwait(false))
                {
                    line.State = DubLineState.Buffered;
                    LineBuffered?.Invoke(line);
                    continue;
                }
                line.State = DubLineState.Generating;
                operationStarted = Stopwatch.GetTimestamp();
                var synthesis = runtime.SynthesizeAsync(
                    new(line.Text, language, voice, null, seed),
                    line.Audio,
                    line.Token);
                playbackAuthorized = await AuthorizeStreamingAsync(line, synthesis, playbackAuthorized).ConfigureAwait(false);
                await synthesis.ConfigureAwait(false);
                RecordMeasurement(line, operationStarted);
                var capturedSamples = await capture.DrainAsync(line.Token).ConfigureAwait(false);
                if (!line.IsTerminal)
                {
                    if (line.VoiceProfileId is not null && line.VoiceProfileHash is not null)
                        await lineCache.StoreAsync(line, line.VoiceProfileId, line.VoiceProfileHash,
                            modelHash, language, seed, capturedSamples, line.Token).ConfigureAwait(false);
                    if (!playbackAuthorized)
                    {
                        line.State = DubLineState.Buffered;
                        LineBuffered?.Invoke(line);
                    }
                }
            }
            catch (OperationCanceledException) when (line.Token.IsCancellationRequested) { }
            catch (Exception error)
            {
                line.State = DubLineState.Failed;
                line.Audio.Complete(error);
                LineFailed?.Invoke(line, error);
            }
            finally
            {
                lock (gate) if (ReferenceEquals(generating, line)) generating = null;
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
            if (charactersPerSecond > 0) meanCharactersPerSecond = Ewma(meanCharactersPerSecond, charactersPerSecond);
        }
    }

    private static double Ewma(double current, double sample) => current * 0.8 + sample * 0.2;

    private async Task<bool> AuthorizeStreamingAsync(DubLine line, Task phase, bool alreadyAuthorized)
    {
        if (alreadyAuthorized || line.ActualStatus != ActualStatus.Actual) return alreadyAuthorized;
        while (!phase.IsCompleted && line.Audio.TotalSamplesWritten < 8400)
            await Task.Delay(10, line.Token).ConfigureAwait(false);
        if (line.IsTerminal || line.Audio.TotalSamplesWritten < 8400) return false;
        line.State = DubLineState.Buffered;
        LineBuffered?.Invoke(line);
        return true;
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

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        ready.Release();
        try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
        lock (gate)
        {
            generating?.Cancel();
            foreach (var item in queue.UnorderedItems) item.Element.Cancel();
            queue.Clear();
        }
        ready.Dispose();
        shutdown.Dispose();
    }
}
