using System.Numerics;
using Resonance.Audio;
using Resonance.Data;
using Resonance.Scheduling;
using Resonance.Tts;
using Resonance.UI;
using Directory = Resonance.Tests.TestDirectory;

namespace Resonance.Tests;

public sealed class CutscenePlaybackControlTests
{
    [Fact]
    public void OnlyEligibleCutsceneDialogueOffersTheControl()
    {
        var applied = new List<bool>();
        var control = new CutscenePlaybackControl(applied.Add);
        control.ObserveLine(1, true);
        Assert.False(control.Snapshot.Visible);
        Assert.False(control.TrySetPaused(control.Snapshot, true));

        control.BeginScene();
        control.ObserveLine(1, false);
        Assert.False(control.Snapshot.Visible);
        Assert.False(control.TrySetPaused(control.Snapshot, true));
        control.ObserveLine(2, true);
        Assert.True(control.Snapshot.Visible);
        Assert.True(control.TrySetPaused(control.Snapshot, true));
        Assert.Equal([true], applied);
    }

    [Fact]
    public void PauseIsPublishedBeforeStoppingWorkAndResumeBeforeRestartingIt()
    {
        CutscenePlaybackControl? control = null;
        var observed = new List<bool>();
        control = new CutscenePlaybackControl(paused =>
        {
            Assert.Equal(paused, control!.IsPaused);
            observed.Add(paused);
        });
        control.BeginScene();
        control.ObserveLine(7, true);
        var initial = control.Snapshot;
        Assert.True(control.TrySetPaused(initial, true));
        Assert.False(control.TrySetPaused(initial, true));
        Assert.True(control.TrySetPaused(control.Snapshot, false));
        Assert.False(control.TrySetPaused(initial, true));
        Assert.False(control.IsPaused);
        Assert.Equal([true, false], observed);
    }

    [Fact]
    public void PauseSurvivesTalkGapsNativeLinesAndSessionRestartsUntilSceneEnds()
    {
        var applied = new List<bool>();
        var control = new CutscenePlaybackControl(applied.Add);
        control.BeginScene();
        control.ObserveLine(10, true);
        Assert.True(control.TrySetPaused(control.Snapshot, true));
        var scene = control.Snapshot.Scene;

        control.HideLine(10);
        control.BeginScene();
        Assert.Equal(scene, control.Snapshot.Scene);
        Assert.True(control.IsPaused);
        Assert.False(control.Snapshot.Visible);
        control.ObserveLine(11, false);
        Assert.True(control.IsPaused);
        Assert.False(control.Snapshot.Visible);
        control.ObserveLine(12, true);
        Assert.True(control.Snapshot.Visible);
        Assert.True(control.IsPaused);
        Assert.Equal([true], applied);

        control.EndScene();
        Assert.False(control.IsPaused);
        Assert.False(control.Snapshot.Visible);
        control.BeginScene();
        control.ObserveLine(13, true);
        Assert.NotEqual(scene, control.Snapshot.Scene);
        Assert.False(control.IsPaused);
        Assert.Equal([true], applied);
    }

    [Fact]
    public void StaleLineOrSceneCannotPauseResumeOrHideANewerPresentation()
    {
        var applied = new List<bool>();
        var control = new CutscenePlaybackControl(applied.Add);
        control.BeginScene();
        control.ObserveLine(1, true);
        var first = control.Snapshot;
        control.ObserveLine(2, true);
        control.HideLine(1);
        Assert.True(control.Snapshot.Visible);
        Assert.False(control.TrySetPaused(first, true));
        Assert.True(control.TrySetPaused(control.Snapshot, true));
        var paused = control.Snapshot;
        control.ObserveLine(3, true);
        Assert.False(control.TrySetPaused(paused, false));
        Assert.True(control.IsPaused);
        control.EndScene();
        control.BeginScene();
        control.ObserveLine(1, true);
        Assert.False(control.TrySetPaused(first, true));
        Assert.Equal([true], applied);
    }

    [Fact]
    public void PauseRemainsPublishedWhenCleanupFails()
    {
        var control = new CutscenePlaybackControl(_ => throw new InvalidOperationException("Stop failed"));
        control.BeginScene();
        control.ObserveLine(1, true);
        Assert.Throws<InvalidOperationException>(() => control.TrySetPaused(control.Snapshot, true));
        Assert.True(control.IsPaused);
    }

    [Fact]
    public async Task PausingCancelsActiveInferenceAndQueuedLookahead()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "resonance-scene-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var cache = new LineCache(database, Path.Combine(root, "cache"), () => 0);
            var runtime = new CancellableRuntime();
            await using var scheduler = new DubScheduler(runtime,
                (_, _) => ValueTask.FromResult(VoiceResolution.Ready(new([], [], 0, 0, ""))),
                cache, "model", "english");
            using var session = new CutsceneSession(8, 1);
            var actual = session.AddActual("npc:1", "First", "Current line", "english");
            var predicted = Assert.Single(session.ReplacePredictions([("npc:2", "Second", "Future line")], "english"));
            var control = new CutscenePlaybackControl(paused =>
            {
                if (!paused) return;
                scheduler.InvalidateEpoch(session.Epoch);
                session.Dispose();
            });
            control.BeginScene();
            control.ObserveLine(10, true);
            scheduler.Enqueue(actual);
            await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            scheduler.Enqueue(predicted);

            Assert.True(control.TrySetPaused(control.Snapshot, true));
            await runtime.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            Assert.True(actual.Token.IsCancellationRequested);
            Assert.True(predicted.Token.IsCancellationRequested);
            Assert.Equal(1, runtime.Requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, 1500, 900, 1360, 864)]
    [InlineData(1920, 100, 1920, 1080, 3420, 1000, 3280, 964)]
    [InlineData(0, 0, 1920, 1080, 2000, 20, 1780, 0)]
    [InlineData(0, 0, 1920, 1080, 20, 1200, 0, 1051)]
    public void OverlayFollowsThePanelAndStaysInsideTheGameViewport(
        float x, float y, float width, float height, float right, float top, float expectedX, float expectedY)
    {
        var position = CutsceneOverlayLayout.AbovePanel(new(x, y), new(width, height),
            new(right, top), new(140, 29), 7);
        Assert.Equal(new Vector2(expectedX, expectedY), position + new Vector2(x, y));
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void OverlayScalesWithTheViewportAndHud(float scale)
    {
        var position = CutsceneOverlayLayout.AbovePanel(Vector2.Zero, new Vector2(1920, 1080) * scale,
            new Vector2(1500, 900) * scale, new Vector2(140, 29) * scale, 7 * scale);
        Assert.Equal(new Vector2(1360, 864) * scale, position);
    }

    private sealed class CancellableRuntime : ITtsRuntime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        public RuntimeCapabilities Capabilities { get; } = new(true, true, true, []);
        public ValueTask<VoiceReference> ExtractReferenceAsync(ReadOnlyMemory<float> pcm, string text, CancellationToken token)
            => ValueTask.FromResult(new VoiceReference([], [], 0, 0, text));
        public async Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token)
        {
            Interlocked.Increment(ref Requests);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { Cancelled.TrySetResult(); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
