using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Resonance.Scheduling;

namespace Resonance.Game;

public sealed class LipSyncService : IAsyncDisposable
{
    private const ushort SpeakNone = 0;
    private const ushort SpeakNormalMiddle = 630;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly object gate = new();
    private readonly object disposeGate = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly HashSet<Task> tasks = [];
    private CancellationTokenSource? active;
    private nint activeActor;
    private int disposed;
    private Task? disposeTask;

    public LipSyncService(IFramework framework, IObjectTable objects)
    {
        this.framework = framework;
        this.objects = objects;
    }

    public void Start(DubLine line)
    {
        Stop();
        if (line.ActorAddress == 0 || Volatile.Read(ref disposed) != 0) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                cancellation.Dispose();
                return;
            }
            active = cancellation;
            activeActor = line.ActorAddress;
        }
        Track(KeepAliveAsync(line.ActorAddress, cancellation));
    }

    public void Stop(bool avoidFrameworkDispatch = false)
    {
        CancellationTokenSource? cancellation;
        nint actor;
        lock (gate)
        {
            cancellation = active;
            actor = activeActor;
            active = null;
            activeActor = 0;
        }
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (actor != 0 && !avoidFrameworkDispatch && Volatile.Read(ref disposed) == 0)
            TrackFramework(framework.Run(() => SetIfPresent(actor, SpeakNone), shutdown.Token));
    }

    private async Task KeepAliveAsync(nint actor, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await framework.Run(() => SetIfPresent(actor, SpeakNormalMiddle), token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { cancellation.Dispose(); }
    }

    private static unsafe void Set(nint actor, ushort timeline)
    {
        if (actor == 0) return;
        ((Character*)actor)->Timeline.SetLipsOverrideTimeline(timeline);
    }

    private void SetIfPresent(nint actor, ushort timeline)
    {
        if (objects.Any(value => value.IsValid() && value.Address == actor)) Set(actor, timeline);
    }

    private void Track(Task task)
    {
        lock (gate) tasks.Add(task);
        _ = ObserveTaskAsync(task);
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
        CancellationTokenSource? cancellation;
        nint actor;
        lock (gate)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            cancellation = active;
            active = null;
            actor = activeActor;
            activeActor = 0;
        }
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        cancellation?.Cancel();
        if (actor != 0 && !avoidFrameworkDispatch)
        {
            try { TrackFramework(framework.Run(() => SetIfPresent(actor, SpeakNone), shutdown.Token)); }
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
        cancellation?.Dispose();
        shutdown.Dispose();
    }
}
