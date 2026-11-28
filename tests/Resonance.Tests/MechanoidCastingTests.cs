using Microsoft.Data.Sqlite;
using Resonance.Data;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class MechanoidCastingTests
{
    [Theory]
    [InlineData(1119, "mechanoid")]
    [InlineData(1107, "mechanoid")]
    [InlineData(1163, "mechanoid")]
    [InlineData(221, "mechanoid")]
    [InlineData(1431, "mechanoid")]
    [InlineData(4890, "mechanoid")]
    [InlineData(2, "mechanoid")]
    [InlineData(253, "mechanoid")]
    [InlineData(2583, "mechanoid")]
    [InlineData(327, "omicron")]
    [InlineData(1892, "omicron")]
    [InlineData(2320, "omicron")]
    [InlineData(2321, "omicron")]
    [InlineData(2323, "omicron")]
    [InlineData(2328, "omicron")]
    [InlineData(2387, "omicron")]
    [InlineData(2389, "omicron")]
    [InlineData(3264, "omicron")]
    [InlineData(3493, "omicron")]
    [InlineData(3494, "omicron")]
    [InlineData(3716, "omicron")]
    [InlineData(3223, "omicron")]
    [InlineData(3313, "omicron")]
    public void IdentifiedMachinesUseTheirOwnSharedCloneRegardlessOfDemographics(long model, string domain)
    {
        var catalog = Catalog();
        foreach (var sex in new[] { "masculine", "feminine" })
        foreach (var age in new[] { "young", "adult", "elder" })
        {
            var evidence = new SpeakerCastingEvidence("npc:unrelated-name", "Ishgard",
                Category: "non_humanoid", ModelCharaId: model, Sex: sex, Age: age);
            var resolution = catalog.Resolve(evidence);
            Assert.Equal(domain, resolution.DomainId);
            Assert.Equal("default", catalog.FallbackVariantId(resolution, evidence));
            Assert.True(catalog.GetDomain(resolution.DomainId).SharedVoice);
        }
    }

    [Theory]
    [InlineData(81)] // Golem
    [InlineData(2707)] // Ronkan doll
    [InlineData(2810)] // Humanoid android
    [InlineData(0)]
    public void MagicalConstructsAndHumanoidFormsDoNotReceiveMachineClones(long model)
    {
        var evidence = new SpeakerCastingEvidence("guidance node", Category: "non_humanoid", ModelCharaId: model);
        Assert.NotEqual("mechanoid", Catalog().Resolve(evidence).DomainId);
        Assert.NotEqual("omicron", Catalog().Resolve(evidence).DomainId);
    }

    [Fact]
    public async Task ExistingAutomaticCastsMigrateOnlyWhenCloneIsInstalledAndExplicitVoicesSurvive()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "mechanoid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var voices = new VoiceRegistry(database);
            var catalog = Catalog();
            var bank = await Speaker("fallback:generic_world:masculine_adult", null);
            var old = await voices.SaveAndAssignAsync(bank.Id, Profile("generic_world", VoiceProfileKind.Designed), token);
            var automatic = await Speaker("npc:machine", 1119);
            var silent = await Speaker("npc:machine-without-voice", 1163);
            var excluded = await Speaker("npc:omicron", 3264);
            var official = await Speaker("npc:official-machine", 1431);
            var custom = await Speaker("npc:custom-machine", 2583);
            foreach (var speaker in new[] { automatic, silent, excluded, official, custom })
                await voices.SaveSpeakerCastingAsync(speaker.Id, "generic_world", "{}", "Generic", 402,
                    catalog.Version, true, token);
            foreach (var speaker in new[] { automatic, excluded })
                await voices.SaveAndAssignAsync(speaker.Id, old, token);
            var otherModel = await voices.SaveAndAssignAsync(automatic.Id,
                Profile("generic_world", VoiceProfileKind.Designed, model: "other"), token);
            var otherLanguage = await voices.SaveAndAssignAsync(automatic.Id,
                Profile("generic_world", VoiceProfileKind.Designed, language: "japanese"), token);
            var officialVoice = await voices.SaveAndAssignAsync(official.Id,
                Profile(null, VoiceProfileKind.Official), token);
            var customVoice = await voices.SaveAndAssignAsync(custom.Id,
                Profile("generic_world", VoiceProfileKind.Designed, "user prompt"), token);
            Assert.Equal(0, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));

            Assert.Equal("generic_world", (await voices.GetSpeakerCastingAsync(automatic.Id, token))?.DomainId);

            var cloneBank = await Speaker("fallback:mechanoid:default", null);
            var clone = await voices.SaveAndAssignAsync(cloneBank.Id, Profile("mechanoid", VoiceProfileKind.Designed), token);
            await database.ReadAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE TRIGGER reject_clone BEFORE INSERT ON speaker_voice WHEN NEW.speaker_id={automatic.Id} BEGIN SELECT RAISE(ABORT,'migration rejected'); END";
                return await command.ExecuteNonQueryAsync(token);
            }, token);
            await Assert.ThrowsAsync<SqliteException>(() => voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));
            Assert.Equal("generic_world", (await voices.GetSpeakerCastingAsync(automatic.Id, token))?.DomainId);
            Assert.Equal(old.Id, (await voices.GetBestVoiceAsync(automatic.Id, "english", "model", token))?.Id);
            await database.ReadAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DROP TRIGGER reject_clone";
                return await command.ExecuteNonQueryAsync(token);
            }, token);
            Assert.Equal(1, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));
            Assert.Equal(clone.Id, (await voices.GetBestVoiceAsync(automatic.Id, "english", "model", token))?.Id);
            Assert.Equal("mechanoid", (await voices.GetSpeakerCastingAsync(automatic.Id, token))?.DomainId);
            Assert.Equal("mechanoid", (await voices.GetSpeakerCastingAsync(silent.Id, token))?.DomainId);
            Assert.Equal("generic_world", (await voices.GetSpeakerCastingAsync(excluded.Id, token))?.DomainId);
            Assert.Equal(old.Id, (await voices.GetBestVoiceAsync(excluded.Id, "english", "model", token))?.Id);
            Assert.Equal(officialVoice.Id, (await voices.GetBestVoiceAsync(official.Id, "english", "model", token))?.Id);
            Assert.Equal(customVoice.Id, (await voices.GetBestVoiceAsync(custom.Id, "english", "model", token))?.Id);
            Assert.Equal(otherModel.Id, (await voices.GetBestVoiceAsync(automatic.Id, "english", "other", token))?.Id);
            Assert.Equal(otherLanguage.Id, (await voices.GetBestVoiceAsync(automatic.Id, "japanese", "model", token))?.Id);
            Assert.Equal(0, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));

            var omicronBank = await Speaker("fallback:omicron:default", null);
            var omicron = await voices.SaveAndAssignAsync(omicronBank.Id, Profile("omicron", VoiceProfileKind.Designed), token);
            Assert.Equal(1, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));
            Assert.Equal(omicron.Id, (await voices.GetBestVoiceAsync(excluded.Id, "english", "model", token))?.Id);
            Assert.Equal("omicron", (await voices.GetSpeakerCastingAsync(excluded.Id, token))?.DomainId);
            Assert.Equal(clone.Id, (await voices.GetBestVoiceAsync(automatic.Id, "english", "model", token))?.Id);
            Assert.Equal(0, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));

            Task<SpeakerIdentity> Speaker(string key, long? model) => voices.ResolveSpeakerAsync(
                key, null, key, 402, "english", new SpeakerMetadata(Race: 0, ModelCharaId: model), token);
            StoredVoiceProfile Profile(string? domain, VoiceProfileKind kind, string? prompt = null,
                string model = "model", string language = "english") =>
                VoiceRegistry.CreateProfile(kind, language, model, null, prompt, null,
                    new VoiceReference([1, 2], [3], 1, 1, domain ?? "official"), "{}", domain, catalog.Version, "{}");
        }
        finally { Directory.Delete(root, true); }
    }

    private static CastingProfileCatalog Catalog() => CastingProfileCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "assets", "dub-profiles.json"));
}
