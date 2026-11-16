namespace Resonance.Scheduling;

public sealed record CutsceneControlSnapshot(long Scene, long TalkSerial, bool Visible, bool Paused)
{
    public static CutsceneControlSnapshot Hidden { get; } = new(0, 0, false, false);
}

public sealed class CutscenePlaybackControl(Action<bool> applyPause)
{
    private CutsceneControlSnapshot snapshot = CutsceneControlSnapshot.Hidden;
    private long nextScene;

    public CutsceneControlSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool IsPaused => Snapshot.Paused;

    // Mutations run on the framework thread; the UI and workers read immutable snapshots.
    public void BeginScene()
    {
        if (Snapshot.Scene != 0) return;
        Volatile.Write(ref snapshot, new(++nextScene, 0, false, false));
    }

    public void EndScene() => Volatile.Write(ref snapshot, CutsceneControlSnapshot.Hidden);

    public void ObserveLine(long serial, bool eligible)
    {
        var current = Snapshot;
        if (current.Scene == 0) return;
        Volatile.Write(ref snapshot, current with { TalkSerial = serial, Visible = eligible });
    }

    public void HideLine(long serial)
    {
        var current = Snapshot;
        if (current.TalkSerial == serial)
            Volatile.Write(ref snapshot, current with { Visible = false });
    }

    public bool TrySetPaused(CutsceneControlSnapshot expected, bool paused)
    {
        var current = Snapshot;
        if (!current.Visible || !ReferenceEquals(current, expected) || current.Paused == paused) return false;
        Volatile.Write(ref snapshot, current with { Paused = paused });
        applyPause(paused);
        return true;
    }
}
