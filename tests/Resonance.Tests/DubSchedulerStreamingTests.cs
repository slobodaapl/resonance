using System.Collections.Concurrent;
using Resonance.Audio;
using Resonance.Data;
using Resonance.Scheduling;
using Resonance.Tts;
using Directory = Resonance.Tests.TestDirectory;

namespace Resonance.Tests;

public sealed class DubSchedulerStreamingTests
{
    [Fact]
    public async Task ActualLineStreamsBeforeCompletionWhenBufferCoversEstimatedRemainder()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-scheduler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var cache = new LineCache(database, Path.Combine(root, "cache"), () => 0);
            var runtime = new BlockingRuntime();
            await using var scheduler = new DubScheduler(runtime, (_, _) =>
                ValueTask.FromResult(VoiceResolution.Ready(new([], [], 0, 0, ""))), cache, "model", "english");
            var buffered = new TaskCompletionSource<DubLine>(TaskCreationOptions.RunContinuationsAsynchronously);
            scheduler.LineBuffered += line => buffered.TrySetResult(line);
            using var line = new DubLine
            {
                SessionEpoch = 1,
                Sequence = 1,
                SpeakerKey = "npc:1",
                SpeakerName = "Test",
                Text = "Streaming line",
                ActualStatus = ActualStatus.Actual,
            };

            scheduler.Enqueue(line);
            Assert.True(line.PredictedAudioDurationSeconds > 0);
            Assert.True(line.EstimatedGenerationSeconds > line.PredictedAudioDurationSeconds * 0.5);
            Assert.True(line.PlaybackDeadline > DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1));
            var authorized = await buffered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Same(line, authorized);
            Assert.False(runtime.Completed.Task.IsCompleted);
            Assert.True(line.Audio.TotalSamplesWritten >= 12_000);
            runtime.Release.TrySetResult();
            await runtime.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task NativeVoiceRuthlesslyCancelsActiveSyntheticGeneration()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-native-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var cache = new LineCache(database, Path.Combine(root, "cache"), () => 0);
            var runtime = new CancellableRuntime();
            await using var scheduler = new DubScheduler(runtime, (_, _) =>
                ValueTask.FromResult(VoiceResolution.Ready(new([], [], 0, 0, ""))), cache, "model", "english");
            using var line = new DubLine
            {
                SessionEpoch = 4,
                Sequence = 12,
                SpeakerKey = "npc:1",
                SpeakerName = "Test",
                Text = "Must be suppressed",
                ActualStatus = ActualStatus.Actual,
            };

            scheduler.Enqueue(line);
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            scheduler.NativeVoiceStarted(4, 12);
            await runtime.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Equal(NativeVoiceStatus.NativeVoiced, line.NativeVoiceStatus);
            Assert.Equal(DubLineState.NativeVoiced, line.State);
            Assert.True(line.Token.IsCancellationRequested);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SchedulerDisposeCancelsActiveGenerationBeforeWorkerExit()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-scheduler-dispose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var cache = new LineCache(database, Path.Combine(root, "cache"), () => 0);
            var runtime = new CancellableRuntime();
            await using var scheduler = new DubScheduler(runtime, (_, _) =>
                ValueTask.FromResult(VoiceResolution.Ready(new VoiceReference([], [], 0, 0, ""))),
                cache, "model", "english");
            using var line = new DubLine
            {
                SessionEpoch = 1,
                Sequence = 1,
                SpeakerKey = "npc:dispose",
                SpeakerName = "Dispose",
                Text = "Dispose me",
                ActualStatus = ActualStatus.Actual,
            };

            scheduler.Enqueue(line);
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await scheduler.DisposeAsync();

            await runtime.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(line.Token.IsCancellationRequested);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SchedulerUsesEachLineLanguageForSynthesis()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-language-lines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var cache = new LineCache(database, Path.Combine(root, "cache"), () => 0);
            var runtime = new LanguageRecordingRuntime();
            await using var scheduler = new DubScheduler(runtime, (_, _) =>
                ValueTask.FromResult(VoiceResolution.Ready(new([], [], 0, 0, ""))), cache, "model", "english");
            var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            scheduler.LineBuffered += _ =>
            {
                if (Interlocked.Increment(ref count) == 2) buffered.TrySetResult();
            };
            using var english = new DubLine
            {
                SessionEpoch = 1,
                Sequence = 1,
                SpeakerKey = "npc:english",
                SpeakerName = "English",
                Text = "English line",
                Language = "english",
                ActualStatus = ActualStatus.Actual,
            };
            using var japanese = new DubLine
            {
                SessionEpoch = 1,
                Sequence = 2,
                SpeakerKey = "npc:japanese",
                SpeakerName = "Japanese",
                Text = "Japanese line",
                Language = "japanese",
                ActualStatus = ActualStatus.Actual,
            };

            scheduler.Enqueue(english);
            scheduler.Enqueue(japanese);
            await buffered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            var requests = runtime.Requests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal("english", requests.Single(request => request.Text == "English line").Language);
            Assert.Equal("japanese", requests.Single(request => request.Text == "Japanese line").Language);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnassignedPredictionDefersWithoutFailureAndSynthesizesAfterPromotion()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-prediction-defer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var cache = new LineCache(database, Path.Combine(root, "cache"), () => 0);
            var runtime = new LanguageRecordingRuntime();
            var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var scheduler = new DubScheduler(runtime, (line, _) =>
            {
                if (line.ActualStatus == ActualStatus.Predicted)
                {
                    deferred.TrySetResult();
                    return ValueTask.FromResult(VoiceResolution.DeferredPrediction);
                }
                return ValueTask.FromResult(VoiceResolution.Ready(new VoiceReference([], [], 0, 0, "")));
            },
                cache, "model", "english");
            var failed = false;
            scheduler.LineFailed += (_, _) => failed = true;
            var buffered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scheduler.LineBuffered += _ => buffered.TrySetResult();
            using var line = new DubLine
            {
                SessionEpoch = 1,
                Sequence = 1,
                SpeakerKey = "name:unassigned",
                SpeakerName = "Unassigned",
                Text = "Future line",
                Language = "english",
                ActualStatus = ActualStatus.Predicted,
            };

            scheduler.Enqueue(line);
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await WaitForStateAsync(line, DubLineState.Predicted, TestContext.Current.CancellationToken);

            Assert.False(failed);
            Assert.Empty(runtime.Requests);

            line.ActualStatus = ActualStatus.Actual;
            scheduler.Enqueue(line);
            await buffered.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.False(failed);
            Assert.Single(runtime.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DeferredPredictionDoesNotReplaceAudioAfterLineBecomesTerminal()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-prediction-terminal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var cache = new LineCache(database, Path.Combine(root, "cache"), () => 0);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<VoiceResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var scheduler = new DubScheduler(
                new LanguageRecordingRuntime(),
                (_, _) =>
                {
                    started.TrySetResult();
                    return new ValueTask<VoiceResolution>(release.Task);
                },
                cache, "model", "english");
            scheduler.BecameIdle += () => idle.TrySetResult();
            using var line = new DubLine
            {
                SessionEpoch = 1,
                Sequence = 1,
                SpeakerKey = "predicted:terminal",
                SpeakerName = "Terminal",
                Text = "Future line",
                Language = "english",
                ActualStatus = ActualStatus.Predicted,
            };

            scheduler.Enqueue(line);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            line.Cancel(DubLineState.Invalidated);
            release.TrySetResult(VoiceResolution.DeferredPrediction);
            await idle.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Equal(DubLineState.Invalidated, line.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AtomicAudioReplacementRejectsTerminalLineAndInvalidatesCandidate()
    {
        using var line = new DubLine
        {
            SessionEpoch = 1,
            Sequence = 1,
            SpeakerKey = "speaker",
            SpeakerName = "Speaker",
            Text = "line",
        };
        Assert.True(line.TryTransition(DubLineState.Generating, DubLineState.Predicted));
        line.Cancel(DubLineState.Invalidated);
        using var replacement = new StreamingAudioBuffer();

        Assert.False(line.TryReplaceAudioAndTransition(
            replacement, DubLineState.Buffered, DubLineState.Generating));
        Assert.Equal(DubLineState.Invalidated, line.State);
        Assert.True(replacement.ProducerCompleted);
    }

    [Fact]
    public void NativePromotionCannotOverwriteTerminalLine()
    {
        using var line = new DubLine
        {
            SessionEpoch = 1,
            Sequence = 1,
            SpeakerKey = "speaker",
            SpeakerName = "Speaker",
            Text = "line",
        };
        Assert.True(line.TryTransition(DubLineState.Completed, DubLineState.Predicted));

        Assert.False(line.TryMarkNativeVoiced(DubLineState.Predicted, DubLineState.Active));
        Assert.Equal(DubLineState.Completed, line.State);
        Assert.Equal(NativeVoiceStatus.Unknown, line.NativeVoiceStatus);
    }

    private static async Task WaitForStateAsync(
        DubLine line, DubLineState expected, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
        while (line.State != expected && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10, token);
        Assert.Equal(expected, line.State);
    }

    private sealed class BlockingRuntime : ITtsRuntime
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeCapabilities Capabilities { get; } = new(true, true, false, true, []);
        public ValueTask<VoiceReference> ExtractReferenceAsync(ReadOnlyMemory<float> monoPcm24Khz, string transcript,
            CancellationToken token) => ValueTask.FromResult(new VoiceReference([], [], 0, 0, transcript));
        public async Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token)
        {
            sink.TryWrite(new float[12_000]);
            await Release.Task.WaitAsync(token);
            sink.TryWrite(new float[100]);
            sink.Complete();
            Completed.TrySetResult();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CancellableRuntime : ITtsRuntime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RuntimeCapabilities Capabilities { get; } = new(true, true, false, true, []);
        public ValueTask<VoiceReference> ExtractReferenceAsync(ReadOnlyMemory<float> monoPcm24Khz, string transcript,
            CancellationToken token) => ValueTask.FromResult(new VoiceReference([], [], 0, 0, transcript));
        public async Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Cancelled.TrySetResult(); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LanguageRecordingRuntime : ITtsRuntime
    {
        public ConcurrentQueue<(string Text, string Language)> Requests { get; } = new();
        public RuntimeCapabilities Capabilities { get; } = new(true, true, false, true, []);
        public ValueTask<VoiceReference> ExtractReferenceAsync(ReadOnlyMemory<float> monoPcm24Khz, string transcript,
            CancellationToken token) => ValueTask.FromResult(new VoiceReference([], [], 0, 0, transcript));
        public Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token)
        {
            Requests.Enqueue((request.Text, request.Language));
            sink.TryWrite(new float[8400]);
            sink.Complete();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
