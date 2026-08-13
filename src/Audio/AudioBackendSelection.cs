namespace Resonance.Audio;

public enum AudioOutputBackend
{
    FfxivGameMixer,
}

public sealed record AudioBackendStatus(
    AudioOutputBackend ActiveBackend,
    bool Configured,
    bool Available,
    bool Healthy,
    bool SceneLocked,
    string Diagnostic);

/// <summary>
/// Locks scene/debug synthesis to FFXIV native voice output. Backend failures
/// are reported; they never silently switch a line to direct-device playback.
/// </summary>
public sealed class AudioBackendSessionLock
{
    public AudioOutputBackend ActiveBackend => AudioOutputBackend.FfxivGameMixer;
    public bool IsSceneLocked { get; private set; }

    public AudioOutputBackend SelectForScene()
    {
        IsSceneLocked = true;
        return ActiveBackend;
    }

    public AudioOutputBackend SelectForDebug()
    {
        return ActiveBackend;
    }

    public AudioOutputBackend EndScene()
    {
        IsSceneLocked = false;
        return ActiveBackend;
    }

    public AudioOutputBackend EndDebug()
    {
        return ActiveBackend;
    }
}
