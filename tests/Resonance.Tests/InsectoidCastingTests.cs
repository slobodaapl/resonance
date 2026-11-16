using Resonance.Data;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class InsectoidCastingTests
{
    [Theory]
    [InlineData(null, "gnath")]
    [InlineData("Gnath", "gnath")]
    [InlineData("Vath", "vath")]
    public void ModelIdentityFindsTheCloneWhileExplicitSpeciesRemainsDistinguishable(string? species, string domain)
    {
        var catalog = Catalog();
        foreach (var sex in new[] { "masculine", "feminine" })
        foreach (var age in new[] { "young", "adult", "elder" })
        {
            var evidence = new SpeakerCastingEvidence("npc:1011928", "Ishgard",
                Category: "non_humanoid", Species: species, ModelCharaId: 832, Sex: sex, Age: age);
            var resolution = catalog.Resolve(evidence);
            Assert.Equal(domain, resolution.DomainId);
            Assert.True(catalog.GetDomain(resolution.DomainId).SharedVoice);
            Assert.Equal("default", catalog.FallbackVariantId(resolution, evidence));
        }
    }

    [Fact]
    public void InsectoidNameDoesNotTurnAHumanoidIntoAnInsect()
    {
        var resolution = Catalog().Resolve(new SpeakerCastingEvidence("Vath Storyteller",
            Category: "humanoid", ModelCharaId: 0, RaceId: 1));
        Assert.DoesNotContain(resolution.DomainId, new[] { "gnath", "vath" });
    }

    [Fact]
    public async Task SavedGenericStorytellerReceivesTheInstalledInsectoidClone()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(AppContext.BaseDirectory, "insectoid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var voices = new VoiceRegistry(database);
            var catalog = Catalog();
            var old = await Bank("generic_world", "masculine_adult");
            var speaker = await voices.ResolveSpeakerAsync("npc:1011928", 1011928, "Vath Storyteller", 398,
                "english", new SpeakerMetadata(Race: 0, ModelCharaId: 832), token);
            await voices.SaveSpeakerCastingAsync(speaker.Id, "generic_world", "{}", "Generic", 398,
                catalog.Version, true, token);
            await voices.SaveAndAssignAsync(speaker.Id, old, token);
            Assert.Equal(0, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));
            Assert.Equal("generic_world", (await voices.GetSpeakerCastingAsync(speaker.Id, token))?.DomainId);

            var clone = await Bank("gnath", "default");
            Assert.Equal(1, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));
            Assert.Equal("gnath", (await voices.GetSpeakerCastingAsync(speaker.Id, token))?.DomainId);
            Assert.Equal(clone.Id, (await voices.GetBestVoiceAsync(speaker.Id, "english", "model", token))?.Id);
            Assert.Equal(0, await voices.ReconcileRegionalAssignmentsAsync(catalog, "model", token));

            async Task<StoredVoiceProfile> Bank(string domain, string variant)
            {
                var bank = await voices.ResolveSpeakerAsync(VoiceRegistry.DomainFallbackSpeakerKey(domain, variant),
                    null, domain, 0, "english", token);
                return await voices.SaveAndAssignAsync(bank.Id, VoiceRegistry.CreateProfile(VoiceProfileKind.Designed,
                    "english", "model", null, null, null, new VoiceReference([1, 2], [3], 1, 1, domain),
                    "{}", domain, catalog.Version, "{}"), token);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static CastingProfileCatalog Catalog() => CastingProfileCatalog.Load(
        Path.Combine(AppContext.BaseDirectory, "assets", "dub-profiles.json"));
}
