using Resonance.Audio;
using Resonance.Scheduling;

namespace Resonance.Tests;

public sealed class AudioEngineActivityTests
{
    [Fact]
    public void SpeakingRequiresAudioStartAndStopClearsItWithoutAFinishCallback()
    {
        var backend = new Backend();
        using var engine = new AudioEngine(backend);
        using var line = Line();
        engine.Play(line, 1);
        Assert.False(engine.IsPlaying);
        backend.Calls[0].Started(line);
        Assert.True(engine.IsPlaying);
        engine.Stop();
        Assert.False(engine.IsPlaying);
        backend.Calls[0].Started(line);
        Assert.False(engine.IsPlaying);
    }

    [Fact]
    public void ReplacedPlaybackIgnoresOldStartFinishAndFailureForSpeakingState()
    {
        var backend = new Backend();
        using var engine = new AudioEngine(backend);
        using var old = Line();
        using var current = Line();
        engine.Play(old, 1);
        engine.Play(current, 1);
        backend.Calls[0].Started(old);
        Assert.False(engine.IsPlaying);
        backend.Calls[1].Started(current);
        Assert.True(engine.IsPlaying);
        backend.Calls[0].Finished(old);
        backend.Calls[0].Failed(old, new IOException("Old playback failed"));
        Assert.True(engine.IsPlaying);
        backend.Calls[1].Finished(current);
        Assert.False(engine.IsPlaying);
    }

    [Fact]
    public void CancellationAndSynchronousBackendFailureCannotLeaveSpeakingLatched()
    {
        var backend = new Backend();
        using var engine = new AudioEngine(backend);
        using var line = Line();
        engine.Play(line, 1);
        backend.Calls[0].Started(line);
        Assert.True(engine.IsPlaying);
        line.Cancel();
        Assert.False(engine.IsPlaying);
        using var failed = Line();
        backend.ThrowAfterStart = true;
        Assert.Throws<IOException>(() => engine.Play(failed, 1));
        Assert.False(engine.IsPlaying);
    }

    private static DubLine Line() => new()
    {
        SessionEpoch = 1, Sequence = 1, SpeakerKey = "test", SpeakerName = "Test", Text = "Voice sample",
    };

    private sealed class Backend : IGameMixerAudioBackend
    {
        public readonly List<(Action<DubLine> Started, Action<DubLine> Finished, Action<DubLine, Exception> Failed)> Calls = [];
        public bool ThrowAfterStart;
        public bool IsAvailable => true;
        public bool IsHealthy => true;
        public string Diagnostic => "Test native audio boundary";
        public Task PrepareAsync(DubLine line, float volume, CancellationToken token) => Task.CompletedTask;
        public void Play(DubLine line, float volume, Action<DubLine> started,
            Action<DubLine> finished, Action<DubLine, Exception> failed)
        {
            line.TryTransition(DubLineState.Active, DubLineState.Predicted);
            Calls.Add((started, finished, failed));
            if (ThrowAfterStart)
            {
                started(line);
                throw new IOException("Native playback failed");
            }
        }
        public void Stop(bool discardPrepared = true) { }
        public void Dispose() { }
    }
}
