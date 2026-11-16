using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Resonance.Scheduling;

namespace Resonance.Game;

public sealed class LipSyncService : IAsyncDisposable
{
    private const ushort SpeakNone = 0;
    private const ushort SpeakNormalMiddle = 630;
    // Replay the 60-frame lip clip before it ends; same-ID override writes do not restart it.
    private static readonly TimeSpan RestartInterval = TimeSpan.FromSeconds(1.5);
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly object gate = new();
    private readonly object disposeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<Task> tasks = [];
    private DubLine? activeLine;
    private DialogueActor? activeActor;
    private TimeSpan sinceRestart;
    private int disposed;
    private Task? disposeTask;

    public LipSyncService(IFramework framework, IObjectTable objects)
    {
        this.framework = framework;
        this.objects = objects;
        framework.Update += OnUpdate;
    }

    public void Start(DubLine line)
    {
        Stop();
        if (line.Actor is not { Address: not 0 } actor || Volatile.Read(ref disposed) != 0) return;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            activeLine = line;
            activeActor = actor;
            sinceRestart = RestartInterval;
            TrackFramework(framework.Run(() => ApplyIfCurrent(line, TimeSpan.Zero), shutdown.Token));
        }
    }

    public void Stop(bool avoidFrameworkDispatch = false) => StopCore(null, avoidFrameworkDispatch);

    public void Stop(DubLine line) => StopCore(line, false);

    private void StopCore(DubLine? expected, bool avoidFrameworkDispatch)
    {
        DialogueActor? actor;
        lock (gate)
        {
            if (expected is not null && !ReferenceEquals(activeLine, expected)) return;
            actor = activeActor;
            activeLine = null;
            activeActor = null;
            if (actor is not null && !avoidFrameworkDispatch && Volatile.Read(ref disposed) == 0)
                TrackFramework(framework.Run(() => ClearIfInactive(actor), shutdown.Token));
        }
    }

    private void OnUpdate(IFramework frame) => ApplyIfCurrent(null, frame.UpdateDelta);

    private void ApplyIfCurrent(DubLine? expected, TimeSpan elapsed)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || activeLine is not { } line
                || expected is not null && !ReferenceEquals(line, expected)
                || activeActor is not { } actor) return;
            if (line.Token.IsCancellationRequested || line.IsTerminal)
            {
                activeLine = null;
                activeActor = null;
                SetIfPresent(actor, SpeakNone);
            }
            else
            {
                sinceRestart += elapsed;
                var restart = sinceRestart >= RestartInterval;
                if (SetIfPresent(actor, SpeakNormalMiddle, restart) && restart)
                    sinceRestart = TimeSpan.Zero;
            }
        }
    }

    private static unsafe void Set(nint actor, ushort timeline, bool restart)
    {
        if (actor == 0) return;
        ((Character*)actor)->Timeline.SetLipsOverrideTimeline(timeline);
        // Cutscene Mode.None ignores LipsOverride; start the lip-slot timeline directly.
        if (restart) ((Character*)actor)->Timeline.TimelineSequencer.PlayTimeline(timeline);
    }

    private void ClearIfInactive(DialogueActor actor)
    {
        lock (gate)
        {
            // A delayed stop must not clear a new line speaking through the same actor.
            if (activeActor != actor) SetIfPresent(actor, SpeakNone);
        }
    }

    private bool SetIfPresent(DialogueActor actor, ushort timeline, bool restart = false)
    {
        var current = objects.SearchById(actor.GameObjectId);
        if (current is ICharacter && current.IsValid()
            && actor.Matches(current.BaseId, current.GameObjectId, current.Address, (byte)current.ObjectKind))
        {
            Set(current.Address, timeline, restart);
            return true;
        }
        return false;
    }

    private void TrackFramework(Task task)
    {
        lock (gate) tasks.Add(task);
        _ = ObserveTaskAsync(task);
    }

    private async Task ObserveTaskAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            lock (gate) tasks.Remove(task);
        }
    }

    public ValueTask DisposeAsync() => DisposeAsync(false);

    public ValueTask DisposeAsync(bool avoidFrameworkDispatch)
    {
        lock (disposeGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            disposeTask = DisposeCoreAsync(avoidFrameworkDispatch);
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync(bool avoidFrameworkDispatch)
    {
        DialogueActor? actor;
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            activeLine = null;
            actor = activeActor;
            activeActor = null;
        }
        framework.Update -= OnUpdate;
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        if (actor is not null && !avoidFrameworkDispatch)
        {
            try { TrackFramework(framework.Run(() => ClearIfInactive(actor))); }
            catch { }
        }
        while (true)
        {
            Task[] pending;
            lock (gate) pending = tasks.Where(task => task.Id != Task.CurrentId).ToArray();
            if (pending.Length == 0) break;
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { }
        }
        shutdown.Dispose();
    }
}
