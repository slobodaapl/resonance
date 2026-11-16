using Resonance.Scheduling;

namespace Resonance.Audio;

/// <summary>
/// Owns playback through FFXIV's native voice mixer. Resonance deliberately
/// exposes no direct operating-system audio endpoint.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private sealed class Playback(DubLine line)
    {
        internal readonly DubLine Line = line;
        internal int Started;
    }

    private readonly IGameMixerAudioBackend? gameMixerBackend;
    private Playback? playback;
    private int disposed;

    public bool IsPlaying
    {
        get
        {
            var current = Volatile.Read(ref playback);
            return current is not null && Volatile.Read(ref current.Started) != 0
                && current.Line.State == DubLineState.Active && !current.Line.Token.IsCancellationRequested;
        }
    }

    public event Action<DubLine>? Started;
    public event Action<DubLine>? Finished;
    public event Action<DubLine, Exception>? Failed;

    public AudioEngine(IGameMixerAudioBackend? gameMixerBackend = null) =>
        this.gameMixerBackend = gameMixerBackend;

    public AudioBackendStatus GetBackendStatus(bool configured, bool sceneLocked)
    {
        var backend = gameMixerBackend;
        return new AudioBackendStatus(
            AudioOutputBackend.FfxivGameMixer,
            configured,
            backend?.IsAvailable == true,
            backend?.IsHealthy == true,
            sceneLocked,
            backend?.Diagnostic ?? "FFXIV game mixer backend is not installed");
    }

    public void Play(DubLine line, float volume)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            line.Dispose();
            return;
        }
        if (line.State == DubLineState.Active) return;
        Stop(discardPreparedGameMixerAssets: false);

        var gameMixer = gameMixerBackend;
        if (gameMixer is null)
        {
            var error = new InvalidOperationException("FFXIV game mixer backend is unavailable");
            line.TryTransition(DubLineState.Failed,
                DubLineState.Active,
                DubLineState.Buffered,
                DubLineState.Generating,
                DubLineState.Queued,
                DubLineState.VoiceResolving,
                DubLineState.Predicted);
            Failed?.Invoke(line, error);
            line.Dispose();
            return;
        }

        var current = new Playback(line);
        Volatile.Write(ref playback, current);
        try
        {
            gameMixer.Play(
                line,
                Math.Clamp(volume, 0f, 2f),
                started =>
                {
                    if (!ReferenceEquals(Volatile.Read(ref playback), current)
                        || started.State != DubLineState.Active || started.Token.IsCancellationRequested) return;
                    Volatile.Write(ref current.Started, 1);
                    Started?.Invoke(started);
                },
                finished =>
                {
                    Interlocked.CompareExchange(ref playback, null, current);
                    Finished?.Invoke(finished);
                },
                (failed, error) =>
                {
                    Interlocked.CompareExchange(ref playback, null, current);
                    Failed?.Invoke(failed, error);
                });
        }
        catch
        {
            Interlocked.CompareExchange(ref playback, null, current);
            throw;
        }
    }

    public void Stop(bool discardPreparedGameMixerAssets = true)
    {
        Interlocked.Exchange(ref playback, null);
        gameMixerBackend?.Stop(discardPreparedGameMixerAssets);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Stop(); }
        finally { gameMixerBackend?.Dispose(); }
    }
}
