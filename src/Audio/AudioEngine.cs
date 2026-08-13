using Resonance.Scheduling;

namespace Resonance.Audio;

/// <summary>
/// Owns playback through FFXIV's native voice mixer. Resonance deliberately
/// exposes no direct operating-system audio endpoint.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly IGameMixerAudioBackend? gameMixerBackend;
    private int disposed;

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
        Stop(discardPreparedGameMixerAssets: false);
        if (Volatile.Read(ref disposed) != 0)
        {
            line.Dispose();
            return;
        }

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

        gameMixer.Play(
            line,
            Math.Clamp(volume, 0f, 2f),
            started => Started?.Invoke(started),
            finished => Finished?.Invoke(finished),
            (failed, error) => Failed?.Invoke(failed, error));
    }

    public void Stop(bool discardPreparedGameMixerAssets = true) =>
        gameMixerBackend?.Stop(discardPreparedGameMixerAssets);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Stop(); }
        finally { gameMixerBackend?.Dispose(); }
    }
}
