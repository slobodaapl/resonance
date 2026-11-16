using System.Collections.Concurrent;
using Resonance.Game;

namespace Resonance.Tests;

public sealed class CutscenePreDubPlannerTests
{
    [Fact]
    public async Task PreparationReturnsBeforeSceneLoadingAndOnlyIncludesTwoSyntheticScenes()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = new DispatchQueue();
        var loaded = new List<CutsceneManifestKey>();
        IReadOnlyList<PreDubScene>? result = null;
        await using var planner = new CutscenePreDubPlanner(key =>
        {
            loaded.Add(key);
            entered.TrySetResult();
            release.Wait(TestContext.Current.CancellationToken);
            return new(key.CutsceneId, "cut", key.CutsceneId == 1
                ? [Line("native", true)]
                : [Line("native", true), Line("choice", choice: true), Line("synthetic")]);
        }, dispatch.Post, error => throw new InvalidOperationException("Unexpected failure", error));
        var operation = Task.Run(async () =>
        {
            var task = planner.Request("plan", [1u, 2u, 3u, 4u], new(0, 2, 3), scenes => result = scenes);
            returned.SetResult();
            await task;
        }, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await returned.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(result);
        }
        finally { release.Set(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        dispatch.Drain();
        var scenes = Assert.IsAssignableFrom<IReadOnlyList<PreDubScene>>(result);
        Assert.Equal([2u, 3u], scenes.Select(scene => scene.CutsceneId));
        Assert.All(scenes, scene => Assert.Equal("synthetic", Assert.Single(scene.Lines).Key));
        Assert.Equal([new(1, 2, 3), new(2, 2, 3), new CutsceneManifestKey(3, 2, 3)], loaded);
    }

    [Fact]
    public async Task RepeatedPollsForTheSamePlanShareOneLoad()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = new DispatchQueue();
        var loads = 0;
        var deliveries = 0;
        await using var planner = new CutscenePreDubPlanner(key =>
        {
            Interlocked.Increment(ref loads);
            entered.SetResult();
            release.Wait(TestContext.Current.CancellationToken);
            return Scene(key);
        }, dispatch.Post, _ => Assert.Fail("Unexpected failure"));
        var first = planner.Request("same", [1u], new(), _ => deliveries++);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            for (var poll = 0; poll < 20; poll++)
                Assert.Same(first, planner.Request("same", [1u], new(), _ => deliveries++));
        }
        finally { release.Set(); }
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        dispatch.Drain();
        await planner.Request("same", [1u], new(), _ => deliveries++);
        dispatch.Drain();
        Assert.Equal(1, loads);
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task NativeOnlyFrontiersAreNotRescannedOnEveryPoll()
    {
        var dispatch = new DispatchQueue();
        var loads = 0;
        var deliveries = 0;
        await using var planner = new CutscenePreDubPlanner(key =>
        {
            loads++;
            return new(key.CutsceneId, "cut", [Line("native", voiced: true)]);
        }, dispatch.Post, _ => Assert.Fail("Unexpected failure"));
        for (var poll = 0; poll < 5; poll++)
        {
            await planner.Request("voiced", [1u, 2u, 3u], new(), scenes =>
            {
                Assert.Empty(scenes);
                deliveries++;
            });
            dispatch.Drain();
        }
        Assert.Equal(3, loads);
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task UnavailableVoicesDoNotConsumeTheSceneBudgetOrReachPreparation()
    {
        var dispatch = new DispatchQueue();
        var checkedLines = new List<string>();
        IReadOnlyList<PreDubScene>? result = null;
        await using var planner = new CutscenePreDubPlanner(key => new(key.CutsceneId, "cut",
            key.CutsceneId switch
            {
                1 => [Line("narration")],
                2 => [Line("native", voiced: true), Line("choice", choice: true)],
                3 => [Line("missing"), Line("ready-first")],
                _ => [Line("ready-next")],
            }), dispatch.Post, _ => Assert.Fail("Unexpected failure"));

        await planner.Request("available", [1u, 2u, 3u, 4u, 5u], new(), scenes => result = scenes,
            (line, _) =>
            {
                checkedLines.Add(line.Key);
                return ValueTask.FromResult(line.Key.StartsWith("ready-", StringComparison.Ordinal));
            });
        dispatch.Drain();

        var scenes = Assert.IsAssignableFrom<IReadOnlyList<PreDubScene>>(result);
        Assert.Equal([3u, 4u], scenes.Select(scene => scene.CutsceneId));
        Assert.Equal(["ready-first", "ready-next"], scenes.SelectMany(scene => scene.Lines).Select(line => line.Key));
        Assert.Equal(["narration", "missing", "ready-first", "ready-next"], checkedLines);
    }

    [Fact]
    public async Task MissingProfilesStayIdleUntilAvailabilityIsInvalidated()
    {
        var dispatch = new DispatchQueue();
        var available = false;
        var checks = 0;
        var deliveries = new List<int>();
        await using var planner = new CutscenePreDubPlanner(Scene, dispatch.Post,
            _ => Assert.Fail("Unexpected failure"));
        ValueTask<bool> CanPrepare(CutsceneVoiceLine _, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            checks++;
            return ValueTask.FromResult(available);
        }

        for (var poll = 0; poll < 5; poll++)
        {
            await planner.Request("same", [1u], new(), scenes => deliveries.Add(scenes.Count), CanPrepare);
            dispatch.Drain();
        }
        Assert.Equal([0], deliveries);
        Assert.Equal(1, checks);

        available = true;
        planner.Cancel();
        await planner.Request("same", [1u], new(), scenes => deliveries.Add(scenes.Count), CanPrepare);
        dispatch.Drain();
        Assert.Equal([0, 1], deliveries);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task QueuedPlanningUsesTheCapturedCandidateList()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = new DispatchQueue();
        var loaded = new List<uint>();
        await using var planner = new CutscenePreDubPlanner(key =>
        {
            loaded.Add(key.CutsceneId);
            if (key.CutsceneId == 1)
            {
                entered.SetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
            return Scene(key);
        }, dispatch.Post, _ => Assert.Fail("Unexpected failure"));
        uint[] candidates = [1, 2];
        var operation = planner.Request("captured", candidates, new(), _ => { });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            candidates[1] = 99;
        }
        finally { release.Set(); }
        await operation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([1u, 2u], loaded);
    }

    [Fact]
    public async Task ANewFrontierCancelsOldWorkAndSerializesAssetLoads()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = new DispatchQueue();
        var delivered = new List<uint>();
        var loaded = new List<uint>();
        await using var planner = new CutscenePreDubPlanner(key =>
        {
            loaded.Add(key.CutsceneId);
            if (key.CutsceneId == 1) { entered.SetResult(); release.Wait(TestContext.Current.CancellationToken); }
            return Scene(key);
        }, dispatch.Post, _ => Assert.Fail("Unexpected failure"));
        var first = planner.Request("old", [1u, 99u], new(), scenes => delivered.Add(scenes[0].CutsceneId));
        Task next;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            next = planner.Request("new", [2u], new(), scenes => delivered.Add(scenes[0].CutsceneId));
            Assert.False(next.IsCompleted);
            Assert.Equal([1u], loaded);
        }
        finally { release.Set(); }
        await Task.WhenAll(first, next).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        dispatch.Drain();
        Assert.Equal([1u, 2u], loaded);
        Assert.Equal([2u], delivered);
    }

    [Fact]
    public async Task CancellationRejectsResultsAlreadyQueuedForTheFramework()
    {
        var dispatch = new DispatchQueue();
        var delivered = false;
        await using var planner = new CutscenePreDubPlanner(Scene, dispatch.Post, _ => Assert.Fail("Unexpected failure"));
        await planner.Request("plan", [1u], new(), _ => delivered = true);
        planner.Cancel();
        dispatch.Drain();
        Assert.False(delivered);
        await planner.Request("plan", [1u], new(), _ => delivered = true);
        dispatch.Drain();
        Assert.True(delivered);
    }

    [Fact]
    public async Task DisposalDrainsTheLoaderWithoutWaitingForFrameworkCallbacks()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatch = new DispatchQueue();
        var delivered = false;
        var planner = new CutscenePreDubPlanner(key =>
        {
            entered.SetResult(); release.Wait(TestContext.Current.CancellationToken); return Scene(key);
        }, dispatch.Post, _ => Assert.Fail("Unexpected failure"));
        var operation = planner.Request("plan", [1u], new(), _ => delivered = true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var disposal = planner.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.Set();
        await Task.WhenAll(operation, disposal).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        dispatch.Drain();
        Assert.False(delivered);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => planner.Request("again", [2u], new(), _ => { }));
    }

    [Fact]
    public async Task FailedLoadingCanRetryTheUnchangedFrontier()
    {
        var dispatch = new DispatchQueue();
        var errors = new List<Exception>();
        var calls = 0;
        var delivered = false;
        await using var planner = new CutscenePreDubPlanner(key =>
        {
            if (++calls == 1) throw new InvalidDataException("missing asset");
            return Scene(key);
        }, dispatch.Post, errors.Add);
        await planner.Request("plan", [1u], new(), _ => delivered = true);
        Assert.IsType<InvalidDataException>(Assert.Single(errors));
        await planner.Request("plan", [1u], new(), _ => delivered = true);
        dispatch.Drain();
        Assert.True(delivered);
    }

    private static CutsceneVoiceLine Line(string key, bool voiced = false, bool choice = false) =>
        new(key, "actor", "dialogue", voiced, null, 0, 0, choice);

    private static CutsceneVoiceManifest Scene(CutsceneManifestKey key) => new(key.CutsceneId, "cut", [Line("line")]);

    private sealed class DispatchQueue
    {
        private readonly ConcurrentQueue<Action> actions = new();
        public void Post(Action action) => actions.Enqueue(action);
        public void Drain()
        {
            while (actions.TryDequeue(out var action)) action();
        }
    }
}
