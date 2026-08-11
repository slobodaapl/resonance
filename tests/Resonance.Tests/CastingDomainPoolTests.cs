using Resonance.Data;
using Resonance.Tts;
using Directory = Resonance.Tests.TestDirectory;

namespace Resonance.Tests;

public sealed class CastingDomainPoolTests
{
    [Fact]
    public async Task ManualRegenerationRefillsInactiveDomainWhenBackgroundCastingIsDisabled()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            await using var pool = CreatePool(database, registry, catalog, "Mist", backgroundEnabled: false);

            await pool.RegenerateDomainAsync("ishgardian", TestContext.Current.CancellationToken);
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));

            Assert.Equal(1, await registry.CountReadyDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(1, await registry.CountReadyDomainPoolAsync(
                "ishgardian", "english", "feminine", TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UnsafeOrCancelledGenerationSavesNoProfileOrPoolRow()
    {
        var unsafeRoot = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(unsafeRoot, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            var safeToWork = true;
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                (_, _, _, _) =>
                {
                    safeToWork = false;
                    return Task.FromResult(ValidReference());
                });
            await using var pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => safeToWork, () => "Mist", () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false);
            await pool.RegenerateDomainAsync("ishgardian", TestContext.Current.CancellationToken);

            Assert.False(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await registry.CountDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(0, await ProfileCountAsync(database));
        }
        finally { Directory.Delete(unsafeRoot, true); }

        var cancelledRoot = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(cancelledRoot, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            using var cancellation = new CancellationTokenSource();
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                (_, _, _, _) =>
                {
                    cancellation.Cancel();
                    return Task.FromResult(ValidReference());
                });
            await using var pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => true, () => "Mist", () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false);
            await pool.RegenerateDomainAsync("ishgardian", TestContext.Current.CancellationToken);

            Assert.False(await pool.ExecuteOneWorkAsync(cancellation.Token));
            Assert.Equal(0, await registry.CountDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(0, await ProfileCountAsync(database));
        }
        finally { Directory.Delete(cancelledRoot, true); }
    }

    [Fact]
    public async Task RegenerationLeavesAssignedProfileUntouched()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            await using var pool = CreatePool(database, registry, catalog, "Mist", backgroundEnabled: false);
            var speaker = await registry.ResolveSpeakerAsync(
                "npc:pool-assigned", 900001, "Pool Assigned", 1, "english",
                TestContext.Current.CancellationToken);

            await pool.RegenerateDomainAsync("ishgardian", TestContext.Current.CancellationToken);
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            var assigned = await registry.TryAssignDomainPoolVoiceAsync(
                speaker.Id, "ishgardian", "english", "masculine", TestContext.Current.CancellationToken);
            Assert.NotNull(assigned);

            await pool.RegenerateDomainAsync("ishgardian", TestContext.Current.CancellationToken);
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));

            var current = await registry.GetBestVoiceAsync(
                speaker.Id, "english", TestContext.Current.CancellationToken);
            Assert.NotNull(current);
            Assert.Equal(assigned!.Id, current.Id);
            Assert.Equal(1, await registry.CountReadyDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TerritoryAndSessionActivationResetPreserveGlobalReadyVoices()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            await using var pool = CreatePool(database, registry, catalog, "Mist", backgroundEnabled: false);

            await pool.RegenerateDomainAsync("ishgardian", TestContext.Current.CancellationToken);
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await registry.CountReadyDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));

            pool.ActivateTerritory("Radz-at-Han");
            Assert.DoesNotContain("ishgardian", pool.Snapshot.ActiveDomains);
            Assert.Equal(1, await registry.CountReadyDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));

            pool.ResetActivation();
            Assert.Empty(pool.Snapshot.ActiveDomains);
            Assert.False(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await registry.CountReadyDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TerritoryModifierContextDoesNotLeakAcrossTerritoryRegeneration()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            var territory = "Radz-at-Han";
            var prompts = new List<string>();
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                (instruction, _, _, _) =>
                {
                    prompts.Add(instruction);
                    return Task.FromResult(new VoiceReference([0.1f], [1], 1, 1, instruction));
                });
            await using var pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => true, () => territory, () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false);

            var radz = catalog.Resolve(new SpeakerCastingEvidence("npc:radz", territory));
            pool.RequestMissingResolution(radz, "english", "masculine");
            await pool.RegenerateDomainAsync("thavnairian", TestContext.Current.CancellationToken);
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));

            territory = "Radz-at-Han";
            var stale = catalog.Resolve(new SpeakerCastingEvidence("npc:stale", territory));
            pool.RequestMissingResolution(stale, "english", "masculine");
            territory = "Thavnair";
            pool.ActivateTerritory(territory);
            await pool.RegenerateDomainAsync("thavnairian", TestContext.Current.CancellationToken);
            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));

            Assert.Equal(2, prompts.Count);
            Assert.Contains("clean metropolitan diction", prompts[0]);
            Assert.DoesNotContain("clean metropolitan diction", prompts[1]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StableEmptyModifierContextFollowsSpeakerAcrossTerritories()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            var territory = "Thavnair";
            var direct = catalog.Resolve(new SpeakerCastingEvidence("stable:thavnair", territory));
            var directPrompt = catalog.BuildPrompt(direct, "english", "masculine");
            Assert.Equal("thavnairian", direct.DomainId);
            Assert.Empty(direct.ModifierIds);
            Assert.DoesNotContain("clean metropolitan diction", directPrompt, StringComparison.Ordinal);

            var prompts = new List<string>();
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                (instruction, _, _, _) =>
                {
                    prompts.Add(instruction);
                    return Task.FromResult(ValidReference(instruction));
                });
            await using var pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => true, () => territory, () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false);

            pool.RequestMissingResolution(direct, "english", "masculine", followsSpeaker: true);
            territory = "Radz-at-Han";
            pool.ActivateTerritory(territory);
            await pool.RegenerateDomainAsync("thavnairian", TestContext.Current.CancellationToken);

            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Single(prompts);
            Assert.DoesNotContain("clean metropolitan diction", prompts[0], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StableExplicitContextSurvivesCancellationAndRetriesWithoutModifierLeak()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            var territory = "Thavnair";
            var resolution = catalog.Resolve(new SpeakerCastingEvidence("stable:retry", territory));
            Assert.Empty(resolution.ModifierIds);
            var prompts = new List<string>();
            var attempts = 0;
            using var cancellation = new CancellationTokenSource();
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                (instruction, _, _, _) =>
                {
                    prompts.Add(instruction);
                    if (++attempts == 1) cancellation.Cancel();
                    return Task.FromResult(ValidReference(instruction));
                });
            await using var pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => true, () => territory, () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false);

            pool.RequestMissingResolution(resolution, "english", "masculine", followsSpeaker: true);
            territory = "Radz-at-Han";
            pool.ActivateTerritory(territory);
            await pool.RegenerateDomainAsync("thavnairian", TestContext.Current.CancellationToken);

            Assert.False(await pool.ExecuteOneWorkAsync(cancellation.Token));
            Assert.Equal(0, await registry.CountDomainPoolAsync(
                "thavnairian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(0, await ProfileCountAsync(database));
            Assert.Single(prompts);
            Assert.DoesNotContain("clean metropolitan diction", prompts[0], StringComparison.Ordinal);

            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await registry.CountDomainPoolAsync(
                "thavnairian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(1, await ProfileCountAsync(database));
            Assert.Equal(2, prompts.Count);
            Assert.DoesNotContain("clean metropolitan diction", prompts[1], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StableExplicitContextSurvivesDesignFailureAndRetriesWithDiagnostics()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            var territory = "Thavnair";
            var resolution = catalog.Resolve(new SpeakerCastingEvidence("stable:failed-retry", territory));
            Assert.Empty(resolution.ModifierIds);
            var prompts = new List<string>();
            var attempts = 0;
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                (instruction, _, _, _) =>
                {
                    prompts.Add(instruction);
                    if (++attempts == 1) throw new InvalidOperationException("test design failure");
                    return Task.FromResult(ValidReference(instruction));
                });
            await using var pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => true, () => territory, () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false);

            pool.RequestMissingResolution(resolution, "english", "masculine", followsSpeaker: true);
            territory = "Radz-at-Han";
            pool.ActivateTerritory(territory);
            await pool.RegenerateDomainAsync("thavnairian", TestContext.Current.CancellationToken);

            Assert.False(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await registry.CountDomainPoolAsync(
                "thavnairian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(0, await ProfileCountAsync(database));
            Assert.Contains("test design failure", pool.Snapshot.Failures);
            Assert.Single(prompts);
            Assert.DoesNotContain("clean metropolitan diction", prompts[0], StringComparison.Ordinal);

            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Equal(1, await registry.CountDomainPoolAsync(
                "thavnairian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(1, await ProfileCountAsync(database));
            Assert.Equal(2, prompts.Count);
            Assert.DoesNotContain("clean metropolitan diction", prompts[1], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StableUrbanModifierContextFollowsSpeakerWithoutVisitingTerritoryModifier()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            var territory = "Solution Nine";
            var direct = catalog.Resolve(new SpeakerCastingEvidence("stable:alexandrian", territory));
            var directPrompt = catalog.BuildPrompt(direct, "english", "masculine");
            Assert.Equal("alexandrian", direct.DomainId);
            Assert.Equal(["urban"], direct.ModifierIds);
            Assert.Contains("clean metropolitan diction", directPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("nostalgic storytelling cadence", directPrompt, StringComparison.Ordinal);

            var prompts = new List<string>();
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                (instruction, _, _, _) =>
                {
                    prompts.Add(instruction);
                    return Task.FromResult(ValidReference(instruction));
                });
            await using var pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => true, () => territory, () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false);

            pool.RequestMissingResolution(direct, "english", "masculine", followsSpeaker: true);
            territory = "Living Memory";
            pool.ActivateTerritory(territory);
            await pool.RegenerateDomainAsync("alexandrian", TestContext.Current.CancellationToken);

            Assert.True(await pool.ExecuteOneWorkAsync(TestContext.Current.CancellationToken));
            Assert.Single(prompts);
            Assert.Contains("clean metropolitan diction", prompts[0], StringComparison.Ordinal);
            Assert.DoesNotContain("nostalgic storytelling cadence", prompts[0], StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RealWorkerManualRequestCancelsBlockedDesignWithoutSaving()
    {
        var root = CreateRoot();
        CastingDomainPool? pool = null;
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var catalog = CastingProfileCatalog.Load(ProjectPath("assets", "dub-profiles.json"));
            using var cadenceSignal = new SemaphoreSlim(0);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var design = new Func<string, long, string, CancellationToken, Task<VoiceReference>>(
                async (_, _, _, token) =>
                {
                    started.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled.TrySetResult();
                        throw;
                    }
                    return ValidReference();
                });
            var cadence = new Func<TimeSpan, CancellationToken, Task>(async (_, token) =>
            {
                await cadenceSignal.WaitAsync(token);
            });
            pool = CastingDomainPool.CreateForTests(
                registry, catalog, design, () => true, () => "Mist", () => "english", () => (1, 0), "test",
                backgroundEnabled: () => false,
                waitForCadence: cadence,
                signalCadence: () => cadenceSignal.Release(),
                startWorker: true);

            await pool.RegenerateDomainAsync("ishgardian", TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            pool.Pause();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await pool.DisposeAsync();
            var snapshot = pool.Snapshot;
            pool = null;

            Assert.Null(snapshot.CurrentGeneration);
            Assert.Empty(snapshot.Failures);
            Assert.Equal(0, await registry.CountDomainPoolAsync(
                "ishgardian", "english", "masculine", TestContext.Current.CancellationToken));
            Assert.Equal(0, await ProfileCountAsync(database));
        }
        finally
        {
            if (pool is not null) await pool.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static CastingDomainPool CreatePool(
        Database database,
        VoiceRegistry registry,
        CastingProfileCatalog catalog,
        string territory,
        bool safeToWork = true,
        bool backgroundEnabled = true) =>
        CastingDomainPool.CreateForTests(
            registry,
            catalog,
            (_, _, _, _) => Task.FromResult(ValidReference()),
            () => safeToWork,
            () => territory,
            () => "english",
            () => (1, 1),
            "test",
            backgroundEnabled: () => backgroundEnabled);

    private static VoiceReference ValidReference(string transcript = "pool") =>
        new([0.1f], [1], 1, 1, transcript);

    private static Task<int> ProfileCountAsync(Database database) => database.ReadAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM voice_profile";
        return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }, TestContext.Current.CancellationToken);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-domain-pool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ProjectPath(params string[] parts)
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "Resonance.csproj")))
        {
            path = Directory.GetParent(path)?.FullName
                ?? throw new DirectoryNotFoundException("Could not locate Resonance project root");
        }
        return Path.Combine([path, .. parts]);
    }
}
