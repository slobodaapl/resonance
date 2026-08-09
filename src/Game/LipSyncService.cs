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
    private CancellationTokenSource? active;
    private nint activeActor;

    public LipSyncService(IFramework framework, IObjectTable objects)
    {
        this.framework = framework;
        this.objects = objects;
    }

    public void Start(DubLine line)
    {
        Stop();
        if (line.ActorAddress == 0) return;
        var cancellation = new CancellationTokenSource();
        lock (gate) { active = cancellation; activeActor = line.ActorAddress; }
        _ = KeepAliveAsync(line.ActorAddress, cancellation.Token);
    }

    public void Stop()
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
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (actor != 0) _ = framework.Run(() => SetIfPresent(actor, SpeakNone));
    }

    private async Task KeepAliveAsync(nint actor, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await framework.Run(() => SetIfPresent(actor, SpeakNormalMiddle), token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(1.5), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
