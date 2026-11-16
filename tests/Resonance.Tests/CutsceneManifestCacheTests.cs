using Resonance.Game;

namespace Resonance.Tests;

public sealed class CutsceneManifestCacheTests
{
    [Fact]
    public void CachedScenesArePartitionedByTextAndVoiceLanguage()
    {
        var loads = new List<CutsceneManifestKey>();
        var cache = new CutsceneManifestCache(key =>
        {
            loads.Add(key);
            return new(key.CutsceneId, "cut", [], []);
        });
        var key = new CutsceneManifestKey(20, 1, 1);
        var first = cache.Get(key);

        Assert.Same(first, cache.Get(key));
        Assert.NotSame(first, cache.Get(key with { TextLanguage = 2 }));
        Assert.NotSame(first, cache.Get(key with { VoiceLanguage = 2 }));
        Assert.Equal(3, loads.Count);
        cache.Clear();
        Assert.NotSame(first, cache.Get(key));
    }

    [Fact]
    public async Task InvalidationDuringLoadingCannotRestoreOldMetadata()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new CutsceneVoiceManifest(20, "old", [], []);
        var updated = new CutsceneVoiceManifest(20, "updated", [], []);
        var count = 0;
        var cache = new CutsceneManifestCache(_ =>
        {
            if (Interlocked.Increment(ref count) != 1) return updated;
            started.SetResult();
            release.Wait(TestContext.Current.CancellationToken);
            return old;
        });
        var key = new CutsceneManifestKey(20, 1, 1);
        var initial = Task.Run(() => cache.Get(key));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var refresh = Task.Run(() => { cache.Clear(); return cache.Get(key); });
            Assert.Same(updated, await refresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally { release.Set(); }
        Assert.Same(old, await initial.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Same(updated, cache.Get(key));
        Assert.Equal(2, count);
    }

    [Fact]
    public void LeastRecentlyUsedScenesAreEvictedWithoutDiscardingTheWorkingSet()
    {
        var counts = new Dictionary<uint, int>();
        var cache = new CutsceneManifestCache(key =>
        {
            counts[key.CutsceneId] = counts.GetValueOrDefault(key.CutsceneId) + 1;
            return new(key.CutsceneId, "cut", [], []);
        });
        for (uint id = 1; id <= 64; id++) cache.Get(new(id, 1, 1));
        cache.Get(new(1, 1, 1));
        cache.Get(new(65, 1, 1));
        cache.Get(new(1, 1, 1));
        cache.Get(new(2, 1, 1));

        Assert.Equal(1, counts[1]);
        Assert.Equal(2, counts[2]);
        Assert.All(counts.Where(value => value.Key != 2), value => Assert.Equal(1, value.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrFailedLoadsCanBeRetried(bool throws)
    {
        var count = 0;
        var ready = new CutsceneVoiceManifest(20, "cut", [], []);
        var cache = new CutsceneManifestCache(_ =>
        {
            if (++count > 1) return ready;
            if (throws) throw new InvalidDataException("unavailable");
            return null;
        });
        var key = new CutsceneManifestKey(20, 1, 1);
        if (throws) Assert.Throws<InvalidDataException>(() => cache.Get(key));
        else Assert.Null(cache.Get(key));
        Assert.Same(ready, cache.Get(key));
        Assert.Equal(2, count);
    }
}
