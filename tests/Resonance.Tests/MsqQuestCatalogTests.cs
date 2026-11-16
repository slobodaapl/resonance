using Resonance.Game;

namespace Resonance.Tests;

public sealed class MsqQuestCatalogTests
{
    [Fact]
    public async Task LoadingDoesNotBlockProgressReadsAndShutdownDrainsIt()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new MsqQuestCatalog(_ =>
        {
            entered.SetResult();
            release.Wait(TestContext.Current.CancellationToken);
            return [new(100, [], [10])];
        });
        var request = Task.Run(() => catalog.TryGet(1), TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Null(await request.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Null(catalog.TryGet(1));
            var disposal = catalog.DisposeAsync().AsTask();
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.Set();
            await catalog.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Null(catalog.TryGet(2));
    }

    [Fact]
    public async Task LoadedCataloguesAreReusedAndPartitionedByLanguage()
    {
        var loads = 0;
        await using var catalog = new MsqQuestCatalog(language =>
        {
            Interlocked.Increment(ref loads);
            return [new((uint)language, [], [10])];
        });
        var first = await Read(catalog, 1);
        Assert.Same(first, catalog.TryGet(1));
        var translated = await Read(catalog, 2);
        Assert.Equal(2u, Assert.Single(translated).QuestId);
        Assert.Same(first, catalog.TryGet(1));
        Assert.Equal(2, loads);
    }

    [Fact]
    public async Task FailedReadIsReportedAndCanBeRetried()
    {
        var loads = 0;
        await using var catalog = new MsqQuestCatalog(_ =>
        {
            if (Interlocked.Increment(ref loads) == 1) throw new InvalidDataException("unavailable sheet");
            return [new(100, [], [10])];
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(catalog, 1));
        Assert.Equal(100u, Assert.Single(await Read(catalog, 1)).QuestId);
        Assert.Equal(2, loads);
    }

    private static async Task<IReadOnlyList<MsqQuestNode>> Read(MsqQuestCatalog catalog, int language)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (catalog.TryGet(language) is { } ready) return ready;
            await Task.Delay(1, timeout.Token);
        }
    }
}
