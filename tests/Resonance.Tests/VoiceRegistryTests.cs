using Microsoft.Data.Sqlite;
using Resonance.Data;
using Resonance.Tts;
using Directory = Resonance.Tests.TestDirectory;

namespace Resonance.Tests;

public sealed class VoiceRegistryTests
{
    [Fact]
    public async Task PoolAssignmentSurvivesDatabaseReopenAndIsNotReassigned()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            long speakerId;
            StoredVoiceProfile assigned;
            using (var database = new Database(path))
            {
                var registry = new VoiceRegistry(database);
                var speaker = await registry.ResolveSpeakerAsync("npc:1049237", 1049237, "Test NPC", 1185,
                    "english", TestContext.Current.CancellationToken);
                speakerId = speaker.Id;
                Assert.Equal((uint)1185, speaker.TerritoryId);
                var first = Profile(VoiceProfileKind.Designed, "first");
                var second = Profile(VoiceProfileKind.Designed, "second");
                await registry.SaveDomainPoolVoiceAsync("ishgardian", "english", "feminine", "noble",
                    "{\"register\":\"noble\"}", first,
                    TestContext.Current.CancellationToken);
                await registry.SaveDomainPoolVoiceAsync("ishgardian", "english", "feminine", "urban",
                    "{\"register\":\"urban\"}", second,
                    TestContext.Current.CancellationToken);
                assigned = Assert.IsType<StoredVoiceProfile>(await registry.TryAssignDomainPoolVoiceAsync(
                    speaker.Id, "ishgardian", "english", "feminine", TestContext.Current.CancellationToken));
                await registry.ClearReadyDomainPoolAsync("ishgardian", "english", TestContext.Current.CancellationToken);
                Assert.Equal(0, await registry.CountReadyDomainPoolAsync("ishgardian", "english", "feminine",
                    TestContext.Current.CancellationToken));
                Assert.Equal(assigned.Id, (await registry.GetBestVoiceAsync(speaker.Id, "english",
                    TestContext.Current.CancellationToken))?.Id);
            }

            using (var reopened = new Database(path))
            {
                var registry = new VoiceRegistry(reopened);
                var speaker = await registry.ResolveSpeakerAsync("npc:1049237", 1049237, "Localized Name", 2048,
                    "english", TestContext.Current.CancellationToken);
                Assert.Equal(speakerId, speaker.Id);
                Assert.Equal((uint)1185, speaker.TerritoryId);
                Assert.Equal(assigned.Id, (await registry.GetBestVoiceAsync(speaker.Id, "english",
                    TestContext.Current.CancellationToken))?.Id);
                var remaining = await registry.TryAssignDomainPoolVoiceAsync(speaker.Id, "ishgardian", "english",
                    "feminine", TestContext.Current.CancellationToken);
                Assert.NotNull(remaining);
                Assert.Equal(assigned.Id, (await registry.GetBestVoiceAsync(speaker.Id, "english",
                    TestContext.Current.CancellationToken))?.Id);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OfficialProfileOutranksDesignedProfile()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync("npc:1", 1, "NPC", 1, "japanese",
                TestContext.Current.CancellationToken);
            var designed = Profile(VoiceProfileKind.Designed, "designed", "japanese");
            var official = Profile(VoiceProfileKind.Official, "official", "japanese");
            await registry.SaveAndAssignAsync(speaker.Id, official, TestContext.Current.CancellationToken);
            await registry.SaveAndAssignAsync(speaker.Id, designed, TestContext.Current.CancellationToken);

            Assert.Equal(official.Id, (await registry.GetBestVoiceAsync(speaker.Id, "japanese",
                TestContext.Current.CancellationToken))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DebugBaseVoiceQueryReturnsLatestOfficialClonePerNameAndLanguage()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var alphinaud = await registry.ResolveSpeakerAsync("npc:alphinaud", 1, "Alphinaud", 1, "english",
                TestContext.Current.CancellationToken);
            var thancred = await registry.ResolveSpeakerAsync("npc:thancred", 2, "Thancred", 1, "english",
                TestContext.Current.CancellationToken);
            await registry.SaveAndAssignAsync(alphinaud.Id, Profile(VoiceProfileKind.Designed, "designed"),
                TestContext.Current.CancellationToken);
            var older = await registry.SaveAndAssignAsync(alphinaud.Id, Profile(VoiceProfileKind.Official, "older"),
                TestContext.Current.CancellationToken);
            var latest = await registry.SaveAndAssignAsync(alphinaud.Id, Profile(VoiceProfileKind.Official, "latest"),
                TestContext.Current.CancellationToken);
            await registry.SaveAndAssignAsync(thancred.Id, Profile(VoiceProfileKind.Official, "japanese", "japanese"),
                TestContext.Current.CancellationToken);

            var english = await registry.GetOfficialVoiceProfilesAsync("english", TestContext.Current.CancellationToken);
            var selected = Assert.Single(english);
            Assert.Equal("Alphinaud", selected.DisplayName);
            Assert.Equal(latest.Id, selected.Profile.Id);
            Assert.NotEqual(older.Id, selected.Profile.Id);
            Assert.Equal(VoiceProfileKind.Official, selected.Profile.Kind);

            var japanese = Assert.Single(await registry.GetOfficialVoiceProfilesAsync(
                "japanese", TestContext.Current.CancellationToken));
            Assert.Equal("Thancred", japanese.DisplayName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingDesignedAssignmentIsReusedWithoutClaimingOrCreatingPoolVoice()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync("npc:designed-existing", 2, "Designed Existing", 1,
                "english", TestContext.Current.CancellationToken);
            var designed = await registry.SaveAndAssignAsync(speaker.Id,
                Profile(VoiceProfileKind.Designed, "already-designed"), TestContext.Current.CancellationToken);
            await registry.SaveDomainPoolVoiceAsync("ishgardian", "english", "feminine", "ready-slot", null,
                Profile(VoiceProfileKind.Designed, "ready-not-claimed"), TestContext.Current.CancellationToken);
            var poolCount = await registry.CountDomainPoolAsync("ishgardian", "english", "feminine",
                TestContext.Current.CancellationToken);
            var readyCount = await registry.CountReadyDomainPoolAsync("ishgardian", "english", "feminine",
                TestContext.Current.CancellationToken);

            var resolved = await registry.TryAssignDomainPoolVoiceAsync(speaker.Id, "ishgardian", "english",
                "feminine", TestContext.Current.CancellationToken);

            Assert.Equal(designed.Id, resolved?.Id);
            Assert.Equal(designed.Id, (await registry.GetBestVoiceAsync(speaker.Id, "english",
                TestContext.Current.CancellationToken))?.Id);
            Assert.Equal(poolCount, await registry.CountDomainPoolAsync("ishgardian", "english", "feminine",
                TestContext.Current.CancellationToken));
            Assert.Equal(readyCount, await registry.CountReadyDomainPoolAsync("ishgardian", "english", "feminine",
                TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestartPreservesOfficialPriorityAndDesignedAssignment()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            long speakerId;
            StoredVoiceProfile designed;
            StoredVoiceProfile official;
            using (var database = new Database(path))
            {
                var registry = new VoiceRegistry(database);
                var speaker = await registry.ResolveSpeakerAsync("npc:restart-priority", 3, "Restart Priority", 1,
                    "english", TestContext.Current.CancellationToken);
                speakerId = speaker.Id;
                designed = await registry.SaveAndAssignAsync(speaker.Id,
                    Profile(VoiceProfileKind.Designed, "restart-designed"), TestContext.Current.CancellationToken);
                official = await registry.SaveAndAssignAsync(speaker.Id,
                    Profile(VoiceProfileKind.Official, "restart-official"), TestContext.Current.CancellationToken);
            }

            using (var reopened = new Database(path))
            {
                var registry = new VoiceRegistry(reopened);
                Assert.Equal(official.Id, (await registry.GetBestVoiceAsync(speakerId, "english",
                    TestContext.Current.CancellationToken))?.Id);

                var assignments = await reopened.ReadAsync(async connection =>
                {
                    var result = new List<(string Id, VoiceProfileKind Kind, int Priority)>();
                    await using var command = connection.CreateCommand();
                    command.CommandText = """
                        SELECT p.id,p.kind,sv.priority
                        FROM speaker_voice sv JOIN voice_profile p ON p.id=sv.profile_id
                        WHERE sv.speaker_id=$speaker
                        ORDER BY sv.priority DESC
                        """;
                    command.Parameters.AddWithValue("$speaker", speakerId);
                    await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                    while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                        result.Add((reader.GetString(0), (VoiceProfileKind)reader.GetInt32(1), reader.GetInt32(2)));
                    return result;
                }, TestContext.Current.CancellationToken);

                Assert.Equal(2, assignments.Count);
                Assert.Equal((official.Id, VoiceProfileKind.Official, 200), assignments[0]);
                Assert.Equal((designed.Id, VoiceProfileKind.Designed, 100), assignments[1]);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DuplicateProfileHashReusesCanonicalStoredId()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var firstSpeaker = await registry.ResolveSpeakerAsync("npc:1", 1, "First", 1, "english",
                TestContext.Current.CancellationToken);
            var secondSpeaker = await registry.ResolveSpeakerAsync("npc:2", 2, "Second", 1, "english",
                TestContext.Current.CancellationToken);
            var first = await registry.SaveAndAssignAsync(firstSpeaker.Id,
                Profile(VoiceProfileKind.Official, "same"), TestContext.Current.CancellationToken);
            var duplicate = await registry.SaveAndAssignAsync(secondSpeaker.Id,
                Profile(VoiceProfileKind.Official, "same"), TestContext.Current.CancellationToken);

            Assert.Equal(first.Id, duplicate.Id);
            Assert.Equal(first.Id, (await registry.GetBestVoiceAsync(secondSpeaker.Id,
                TestContext.Current.CancellationToken))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PoolSequenceNeverReusesSeedsAfterReadyVoicesAreDeleted()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            using (var database = new Database(path))
            {
                var registry = new VoiceRegistry(database);
                Assert.Equal(0, await registry.ReserveDomainPoolSequenceAsync("thavnairian", "french", "masculine",
                    TestContext.Current.CancellationToken));
                Assert.Equal(1, await registry.ReserveDomainPoolSequenceAsync("thavnairian", "french", "masculine",
                    TestContext.Current.CancellationToken));
                await registry.ClearReadyDomainPoolAsync("thavnairian", "french", TestContext.Current.CancellationToken);
            }

            using (var reopened = new Database(path))
            {
                var registry = new VoiceRegistry(reopened);
                Assert.Equal(2, await registry.ReserveDomainPoolSequenceAsync("thavnairian", "french", "masculine",
                    TestContext.Current.CancellationToken));
                Assert.Equal(0, await registry.ReserveDomainPoolSequenceAsync("thavnairian", "french", "feminine",
                    TestContext.Current.CancellationToken));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OfficialProfileInAnotherLanguageDoesNotSupersedeDesignedCurrentLanguage()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync("npc:language", 10, "Language NPC", 1, "english",
                TestContext.Current.CancellationToken);
            var english = await registry.SaveAndAssignAsync(speaker.Id,
                Profile(VoiceProfileKind.Designed, "english-designed", "english"),
                TestContext.Current.CancellationToken);
            var japanese = await registry.SaveAndAssignAsync(speaker.Id,
                Profile(VoiceProfileKind.Official, "japanese-official", "japanese"),
                TestContext.Current.CancellationToken);

            var currentLanguage = await registry.GetBestVoiceAsync(speaker.Id, "english",
                TestContext.Current.CancellationToken);
            Assert.Equal(english.Id, currentLanguage?.Id);
            Assert.Equal(VoiceProfileKind.Designed, currentLanguage?.Kind);

            var otherLanguage = await registry.GetBestVoiceAsync(speaker.Id, "japanese",
                TestContext.Current.CancellationToken);
            Assert.Equal(japanese.Id, otherLanguage?.Id);
            Assert.Equal(VoiceProfileKind.Official, otherLanguage?.Kind);
            Assert.Null(await registry.GetBestVoiceAsync(speaker.Id, "german",
                TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PoolClaimScoresKnownTraitsAndNeverAssignsOneProfileTwice()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync("npc:traits", 11, "Trait NPC", 1, "english",
                TestContext.Current.CancellationToken);
            await registry.SaveSpeakerCastingAsync(speaker.Id, "ishgardian",
                "{\"register\":\"noble\"}", "identity", 1, 1, true,
                TestContext.Current.CancellationToken);
            var noble = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, "english", "model", 1,
                "noble", 1, new VoiceReference([.1f], [1], 1, 1, "noble"),
                domainId: "ishgardian", catalogVersion: 1, traitsJson: "{\"register\":\"noble\"}");
            var urban = VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, "english", "model", 1,
                "urban", 2, new VoiceReference([.2f], [2], 1, 1, "urban"),
                domainId: "ishgardian", catalogVersion: 1, traitsJson: "{\"register\":\"urban\"}");
            await registry.SaveDomainPoolVoiceAsync("ishgardian", "english", "feminine", "noble",
                "{\"register\":\"noble\"}", noble, TestContext.Current.CancellationToken);
            await registry.SaveDomainPoolVoiceAsync("ishgardian", "english", "feminine", "urban",
                "{\"register\":\"urban\"}", urban, TestContext.Current.CancellationToken);

            var assigned = await registry.TryAssignDomainPoolVoiceAsync(speaker.Id, "ishgardian", "english",
                "feminine", TestContext.Current.CancellationToken);
            Assert.Equal(noble.Id, assigned?.Id);

            var secondSpeaker = await registry.ResolveSpeakerAsync("npc:traits-2", 12, "Trait NPC 2", 1, "english",
                TestContext.Current.CancellationToken);
            var second = await registry.TryAssignDomainPoolVoiceAsync(secondSpeaker.Id, "ishgardian", "english",
                "feminine", "{\"register\":\"noble\"}", TestContext.Current.CancellationToken);
            Assert.Equal(urban.Id, second?.Id);

            var thirdSpeaker = await registry.ResolveSpeakerAsync("npc:traits-3", 13, "Trait NPC 3", 1, "english",
                TestContext.Current.CancellationToken);
            Assert.Null(await registry.TryAssignDomainPoolVoiceAsync(thirdSpeaker.Id, "ishgardian", "english",
                "feminine", TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OfficialProfilesCannotEnterDomainPools()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var official = Profile(VoiceProfileKind.Official, "official");

            await Assert.ThrowsAsync<InvalidOperationException>(() => registry.SaveDomainPoolVoiceAsync(
                "ishgardian", "english", "feminine", "default", null, official,
                TestContext.Current.CancellationToken));
            Assert.Equal(0, await registry.CountDomainPoolAsync("ishgardian", "english", "feminine",
                TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PoolReadyStateRejectsAssignedSpeakerRows()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync("npc:ready-state", 16, "Ready State NPC", 1,
                "english", TestContext.Current.CancellationToken);
            var profile = Profile(VoiceProfileKind.Designed, "ready-state");
            await registry.SaveDomainPoolVoiceAsync("ishgardian", "english", "feminine", "default", null,
                profile, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<SqliteException>(() => database.WriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE pool_voice SET assigned_speaker_id=$speaker WHERE profile_id=$profile";
                command.Parameters.AddWithValue("$speaker", speaker.Id);
                command.Parameters.AddWithValue("$profile", profile.Id);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken));
            Assert.Equal(1, await registry.CountReadyDomainPoolAsync("ishgardian", "english", "feminine",
                TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StableCastingSurvivesCatalogUpdateAndReopen()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            long speakerId;
            using (var database = new Database(path))
            {
                var registry = new VoiceRegistry(database);
                var speaker = await registry.ResolveSpeakerAsync("npc:stable-casting", 14, "Stable NPC", 1, "english",
                    TestContext.Current.CancellationToken);
                speakerId = speaker.Id;
                await registry.SaveSpeakerCastingAsync(speaker.Id, "ishgardian", "{\"register\":\"noble\"}",
                    "identity", 1, 1, true, TestContext.Current.CancellationToken);
                var changed = await registry.SaveSpeakerCastingAsync(speaker.Id, "thavnairian", "{\"register\":\"urban\"}",
                    "catalog-update", 2, 2, true, TestContext.Current.CancellationToken);
                Assert.Equal("ishgardian", changed.DomainId);
                Assert.True(changed.IsStable);
            }

            using (var reopened = new Database(path))
            {
                var registry = new VoiceRegistry(reopened);
                var casting = await registry.GetSpeakerCastingAsync(speakerId,
                    TestContext.Current.CancellationToken);
                Assert.NotNull(casting);
                Assert.Equal("ishgardian", casting.DomainId);
                Assert.Equal(1, casting.CatalogVersion);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SpeakerEvidenceUpsertDoesNotEraseKnownNullableFields()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var first = await registry.ResolveSpeakerAsync("npc:evidence", 15, "Evidence NPC", 1, "english",
                new SpeakerMetadata(Gender: 1, Race: 2, Tribe: 3, Body: 4, Height: 5, MuscleMass: 6,
                    ModelCharaId: 7, Sex: "Feminine", BodyType: "Muscular", Age: "Adult",
                    Culture: "Ishgardian", VariantTraitsJson: "{\"register\":\"noble\"}", EvidenceSource: "live"),
                TestContext.Current.CancellationToken);
            var second = await registry.ResolveSpeakerAsync("npc:evidence", null, "Localized Evidence NPC", 2, "english",
                new SpeakerMetadata(Race: 9, Culture: "Sharlayan"),
                TestContext.Current.CancellationToken);

            Assert.Equal(first.Id, second.Id);
            Assert.Equal(1, second.Gender);
            Assert.Equal(9, second.Race);
            Assert.Equal(3, second.Tribe);
            Assert.Equal(4, second.Body);
            Assert.Equal(5, second.Height);
            Assert.Equal(6, second.MuscleMass);
            Assert.Equal(7, second.ModelCharaId);
            Assert.Equal("feminine", second.Sex);
            Assert.Equal("muscular", second.BodyType);
            Assert.Equal("adult", second.Metadata?.Age);
            Assert.Equal("sharlayan", second.Metadata?.Culture);
            Assert.Equal("noble", second.Metadata?.Register);
            Assert.Equal("live", second.Metadata?.EvidenceSource);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExactNormalizedDisplayNameFindsExistingLanguageModelAssignment()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync(
                "npc:leih", 1, "Leih Aliapoh", 1, "english",
                TestContext.Current.CancellationToken);
            var profile = await registry.SaveAndAssignAsync(
                speaker.Id, Profile(VoiceProfileKind.Designed, "leih"),
                TestContext.Current.CancellationToken);

            var match = await registry.GetBestVoiceByDisplayNameAsync(
                "LEIHALIAPOH", "english", "model", TestContext.Current.CancellationToken);

            Assert.NotNull(match);
            Assert.Equal(speaker.StableKey, match.StableKey);
            Assert.Equal(profile.Id, match.Profile.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AmbiguousNormalizedDisplayNameDoesNotGuessPersistentIdentity()
    {
        var root = CreateRoot();
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            foreach (var (key, name) in new[] { ("npc:a", "Same Name"), ("npc:b", "Same-Name") })
            {
                var speaker = await registry.ResolveSpeakerAsync(
                    key, null, name, 1, "english", TestContext.Current.CancellationToken);
                await registry.SaveAndAssignAsync(
                    speaker.Id, Profile(VoiceProfileKind.Designed, key),
                    TestContext.Current.CancellationToken);
            }

            Assert.Null(await registry.GetBestVoiceByDisplayNameAsync(
                "SAMENAME", "english", "model", TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }

    private static StoredVoiceProfile Profile(VoiceProfileKind kind, string text, string language = "english") =>
        VoiceRegistry.CreateProfile(kind, language, "model", 1, text, 42,
            new VoiceReference([.25f], [1, 2], 1, 2, text));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-voice-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
