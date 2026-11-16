using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Bootstrap;
using Resonance.Data;
using Resonance.Game;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed partial class OfficialProfilePackManagerTests
{
    [Fact]
    public async Task PackImportPreservesExistingOfficialNpcAndCanonicalPreferences()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory,"canonical-preference-"+Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root,"data","official-profile-pack");
        Directory.CreateDirectory(local);
        try
        {
            var path = Path.Combine(local,"official-profiles.sqlite3");
            CreatePackDatabase(path,1024,"model-q4");
            await File.WriteAllTextAsync(Path.Combine(local,"official-profile-pack.json"),JsonSerializer.Serialize(new
            {
                SchemaVersion=1,PackVersion=7,Url="https://example.invalid/pack.zip",Length=1,
                Sha256=new string('a',64),DatabaseSha256=await HashAsync(path),
            }),token);
            using var database = new Database(Path.Combine(root,"user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var catalog = OfficialVoiceCatalog.Parse("""
                {"schemaVersion":1,"catalogVersion":1,"groups":[{"id":"actor","label":"Actor",
                "npcBaseIds":[101],"aliases":{},"sources":{},"actorTokens":["ACTOR"]}]}
                """);
            using var manager = new OfficialProfilePackManager(Path.Combine(root,"data"),database,voices,catalog,CastingCatalog());
            var npc = await voices.ResolveSpeakerAsync("npc:101",101,"Actor",1,"english",token);
            var captured = await voices.SaveAndAssignAsync(npc.Id,VoiceRegistry.CreateProfile(
                VoiceProfileKind.Official,"english","model-q4",null,null,null,
                new VoiceReference([0.3f],[2],1,1,"existing NPC reference"),"{}"),token);
            var canonical = await voices.ResolveSpeakerAsync("official:actor",null,"Actor",0,"english",token);
            var original = await voices.SaveAndAssignAsync(canonical.Id,VoiceRegistry.CreateProfile(
                VoiceProfileKind.Official,"english","model-q4",null,null,null,
                new VoiceReference([0.4f],[3],1,1,"existing canonical reference"),"{}"),token);
            Assert.Equal(1,(await manager.SynchronizeAsync("model-q4",token,allowRemoteUpdate:false)).ImportedProfiles);
            Assert.Equal(original.Id,(await voices.GetBestVoiceByStableKeyAsync("official:actor","english","model-q4",token))?.Id);
            var preferred = await voices.SaveAndAssignAsync(canonical.Id,VoiceRegistry.CreateProfile(
                VoiceProfileKind.Official,"english","model-q4",null,null,null,
                new VoiceReference([0.2f],[1],1,1,"preferred reference"),"{}"),token);

            var repeat = await manager.SynchronizeAsync("model-q4",token,allowRemoteUpdate:false);

            Assert.Equal(0,repeat.ImportedProfiles);
            Assert.Equal(preferred.Id,(await voices.GetBestVoiceByStableKeyAsync("official:actor","english","model-q4",token))?.Id);
            Assert.Equal(captured.Id,(await voices.GetBestVoiceAsync(npc.Id,"english","model-q4",token))?.Id);
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public async Task LocalizedAliasesRepairAssignmentsWithoutAnInstalledPackAndPreserveCanonicalOverride()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "official-reconciliation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var npc = await voices.ResolveSpeakerAsync("npc:101", 101, "クルル", 1, "japanese", token);
            await voices.ResolveSpeakerAsync("npc:101", 101, "Unidentified", 1, "english", token);
            var canonical = await voices.ResolveSpeakerAsync("official:krile", null, "Krile", 0, "english", token);
            var reference = new VoiceReference([0.1f], [1], 1, 1, "reference");
            var older = VoiceRegistry.CreateProfile(VoiceProfileKind.Official, "english", "model", null,
                null, null, reference);
            var captured = VoiceRegistry.CreateProfile(VoiceProfileKind.Official, "english", "model", null,
                null, null, reference with { Transcript = "captured" });
            await voices.SaveAndAssignAsync(canonical.Id, captured, token);
            await voices.SaveAndAssignAsync(canonical.Id, older, token);
            await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE voice_profile SET created_utc=CASE WHEN id=$captured
                      THEN '2020-01-01T00:00:00Z' ELSE '2000-01-01T00:00:00Z' END
                    WHERE id IN ($captured,$older)
                    """;
                command.Parameters.AddWithValue("$captured", captured.Id);
                command.Parameters.AddWithValue("$older", older.Id);
                return await command.ExecuteNonQueryAsync(token);
            }, token);
            await voices.SaveAndAssignAsync(npc.Id, VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed, "english", "model", 1, "designed", 1, reference), token);
            var catalog = OfficialVoiceCatalog.Parse("""
                {"schemaVersion":1,"catalogVersion":1,"groups":[{"id":"krile","label":"Krile",
                "npcBaseIds":[],"aliases":{"japanese":["クルル"]},"sources":{},"actorTokens":["KRILE"]}]}
                """);
            using var manager = new OfficialProfilePackManager(Path.Combine(root, "data"), database, voices,
                catalog, CastingCatalog());

            var result = await manager.SynchronizeAsync("model", token, allowRemoteUpdate: false);

            Assert.Equal(0, result.ImportedProfiles);
            Assert.Equal(1, result.ReconciledAssignments);
            Assert.Equal(captured.Id, (await voices.GetBestVoiceAsync(npc.Id, "english", "model", token))?.Id);
            Assert.Equal(captured.Id, (await voices.GetBestVoiceAsync(canonical.Id, "english", "model", token))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConflictingStoredAliasesDoNotChooseAnOfficialCharacter()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "official-reconciliation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var npc = await voices.ResolveSpeakerAsync("npc:101", 101, "First", 1, "english", token);
            await voices.ResolveSpeakerAsync("npc:101", 101, "Second", 1, "english", token);
            var reference = new VoiceReference([0.1f], [1], 1, 1, "reference");
            var designed = await voices.SaveAndAssignAsync(npc.Id, VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed, "english", "model", 1, "designed", 1, reference), token);
            foreach (var group in new[] { "first", "second" })
            {
                var canonical = await voices.ResolveSpeakerAsync($"official:{group}", null, group, 0, "english", token);
                await voices.SaveAndAssignAsync(canonical.Id, VoiceRegistry.CreateProfile(
                    VoiceProfileKind.Official, "english", "model", null, null, null,
                    reference with { Transcript = group }), token);
            }
            var catalog = OfficialVoiceCatalog.Parse("""
                {"schemaVersion":1,"catalogVersion":1,"groups":[
                {"id":"first","label":"First","npcBaseIds":[],"aliases":{"english":["First"]},"sources":{}},
                {"id":"second","label":"Second","npcBaseIds":[],"aliases":{"english":["Second"]},"sources":{}}]}
                """);

            Assert.Equal(0, await voices.ReconcileOfficialAssignmentsAsync(catalog, "model", token));
            Assert.Equal(designed.Id, (await voices.GetBestVoiceAsync(npc.Id, "english", "model", token))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedReconciliationRollsBackAllNpcAssignments()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "official-reconciliation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var first = await voices.ResolveSpeakerAsync("npc:101", 101, "First", 1, "english", token);
            var second = await voices.ResolveSpeakerAsync("npc:102", 102, "Second", 1, "english", token);
            var canonical = await voices.ResolveSpeakerAsync("official:actor", null, "Actor", 0, "english", token);
            var reference = new VoiceReference([0.1f], [1], 1, 1, "reference");
            var designed = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, "english", "model", 1,
                "designed", 1, reference);
            await voices.SaveAndAssignAsync(first.Id, designed, token);
            await voices.SaveAndAssignAsync(second.Id, designed, token);
            await voices.SaveAndAssignAsync(canonical.Id, VoiceRegistry.CreateProfile(
                VoiceProfileKind.Official, "english", "model", null, null, null, reference), token);
            await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $$"""
                    CREATE TRIGGER reject_assignment BEFORE INSERT ON speaker_voice
                    WHEN NEW.speaker_id={{second.Id}} AND NEW.priority=200
                    BEGIN SELECT RAISE(ABORT,'injected assignment failure'); END;
                    """;
                return await command.ExecuteNonQueryAsync(token);
            }, token);
            var catalog = OfficialVoiceCatalog.Parse("""
                {"schemaVersion":1,"catalogVersion":1,"groups":[{"id":"actor","label":"Actor",
                "npcBaseIds":[101,102],"aliases":{},"sources":{},"actorTokens":["ACTOR"]}]}
                """);

            await Assert.ThrowsAsync<SqliteException>(() => voices.ReconcileOfficialAssignmentsAsync(catalog, "model", token));
            Assert.Equal(designed.Id, (await voices.GetBestVoiceAsync(first.Id, "english", "model", token))?.Id);
            Assert.Equal(designed.Id, (await voices.GetBestVoiceAsync(second.Id, "english", "model", token))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedPackRepairsExistingNpcAssignmentsAfterCatalogEnrichment(bool byNpcId)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "official-reconciliation-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(root, "data", "official-profile-pack");
        Directory.CreateDirectory(local);
        try
        {
            var path = Path.Combine(local, "official-profiles.sqlite3");
            CreatePackDatabase(path, 1024, "model-q4", "model-q8");
            await File.WriteAllTextAsync(Path.Combine(local, "official-profile-pack.json"), JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, PackVersion = 7, Url = "https://example.invalid/pack.zip", Length = 1,
                Sha256 = new string('a', 64), DatabaseSha256 = await HashAsync(path),
            }), token);
            using var database = new Database(Path.Combine(root, "user.sqlite3"));
            var voices = new VoiceRegistry(database);
            var npc = await voices.ResolveSpeakerAsync("npc:101", 101, "Actor Full Name", 1, "english", token);
            var unrelated = await voices.ResolveSpeakerAsync("npc:999", 999, "Unrelated", 1, "english", token);
            var battle = await voices.ResolveSpeakerAsync("bnpc:101", 101, "Actor Full Name", 1, "english", token);
            StoredVoiceProfile Designed(string language, string model) => VoiceRegistry.CreateProfile(
                VoiceProfileKind.Designed, language, model, 1, "designed", 1,
                new VoiceReference([0.1f], [1], 1, 1, "designed"));
            var designed = Designed("english", "model-q4");
            foreach (var speaker in new[] { npc, unrelated, battle })
                await voices.SaveAndAssignAsync(speaker.Id, designed, token);
            var otherModel = await voices.SaveAndAssignAsync(npc.Id, Designed("english", "model-q8"), token);
            var otherLanguage = await voices.SaveAndAssignAsync(npc.Id, Designed("japanese", "model-q4"), token);

            var original = OfficialVoiceCatalog.Parse("""
                {"schemaVersion":1,"catalogVersion":1,"groups":[{"id":"actor","label":"Actor",
                "npcBaseIds":[],"aliases":{},"sources":{},"actorTokens":["ACTOR"]}]}
                """);
            using (var manager = new OfficialProfilePackManager(Path.Combine(root, "data"), database, voices,
                       original, CastingCatalog()))
                await manager.SynchronizeAsync("model-q4", token, allowRemoteUpdate: false);

            var enriched = OfficialVoiceCatalog.Parse($$"""
                {"schemaVersion":1,"catalogVersion":2,"groups":[{"id":"actor","label":"Actor",
                "npcBaseIds":{{(byNpcId ? "[101]" : "[]")}},
                "aliases":{"english":["{{(byNpcId ? "Unseen Alias" : "Actor Full Name")}}"]},
                "sources":{},"actorTokens":["ACTOR"]}]}
                """);
            using var updated = new OfficialProfilePackManager(Path.Combine(root, "data"), database, voices,
                enriched, CastingCatalog());
            var repaired = await updated.SynchronizeAsync("model-q4", token, allowRemoteUpdate: false);

            Assert.Equal(0, repaired.ImportedProfiles);
            var canonical = await voices.GetBestVoiceByStableKeyAsync("official:actor", "english", "model-q4", token);
            Assert.NotNull(canonical);
            Assert.Equal(VoiceProfileKind.Official, canonical.Kind);
            Assert.Equal(canonical.Id, (await voices.GetBestVoiceAsync(npc.Id, "english", "model-q4", token))?.Id);
            Assert.Equal(1, repaired.ReconciledAssignments);
            Assert.Equal(designed.Id, (await voices.GetBestVoiceAsync(unrelated.Id, "english", "model-q4", token))?.Id);
            Assert.Equal(designed.Id, (await voices.GetBestVoiceAsync(battle.Id, "english", "model-q4", token))?.Id);
            Assert.Equal(otherModel.Id, (await voices.GetBestVoiceAsync(npc.Id, "english", "model-q8", token))?.Id);
            Assert.Equal(otherLanguage.Id, (await voices.GetBestVoiceAsync(npc.Id, "japanese", "model-q4", token))?.Id);

            var repeated = await updated.SynchronizeAsync("model-q4", token, allowRemoteUpdate: false);
            Assert.Equal(0, repeated.ImportedProfiles);
            Assert.Equal(0, repeated.ReconciledAssignments);
            Assert.Equal(canonical.Id, (await voices.GetBestVoiceAsync(npc.Id, "english", "model-q4", token))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }
}
