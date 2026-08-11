using Resonance.Audio;
using Resonance.Data;
using Resonance.Game;
using Resonance.Tts;
using Directory = Resonance.Tests.TestDirectory;

namespace Resonance.Tests;

public sealed class OfficialReferenceBuilderTests
{
    [Fact]
    public async Task LegacyUnknownClipIsAdoptedOnlyForTheObservedLanguage()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            using var database = new Database(path);
            await database.WriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO speaker(stable_key,display_name,first_territory,created_utc)
                    VALUES('npc:legacy-reference','Legacy Reference',1,'now');
                    INSERT INTO official_reference_clip(
                      speaker_id,source_hash,language,transcript,duration_seconds,created_utc)
                    VALUES(
                      (SELECT id FROM speaker WHERE stable_key='npc:legacy-reference'),
                      'legacy-source','und','Observed line',1.0,'now');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            var registry = new VoiceRegistry(database);
            await using var builder = NewBuilder(database, registry, root);
            var speakerId = await SpeakerIdAsync(database);

            await builder.AddPcmAsync(speakerId, "legacy-source", "Observed line", "en",
                Pcm(), TestContext.Current.CancellationToken);

            var language = await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT language FROM official_reference_clip WHERE speaker_id=$speaker AND source_hash='legacy-source'";
                command.Parameters.AddWithValue("$speaker", speakerId);
                return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            Assert.Equal("english", language);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LegacyUnknownClipIsNotAdoptedWhenExactLanguageAlreadyExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            using var database = new Database(path);
            await database.WriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO speaker(stable_key,display_name,first_territory,created_utc)
                    VALUES('npc:legacy-existing','Legacy Existing',1,'now');
                    INSERT INTO official_reference_clip(
                      speaker_id,source_hash,language,transcript,duration_seconds,created_utc)
                    VALUES
                      ((SELECT id FROM speaker WHERE stable_key='npc:legacy-existing'),
                        'shared-source','und','Legacy line',1.0,'now'),
                      ((SELECT id FROM speaker WHERE stable_key='npc:legacy-existing'),
                        'shared-source','english','English line',1.0,'now');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            var registry = new VoiceRegistry(database);
            await using var builder = NewBuilder(database, registry, root);
            var speakerId = await SpeakerIdAsync(database);

            await builder.AddPcmAsync(speakerId, "shared-source", "English line", "english",
                Pcm(), TestContext.Current.CancellationToken);

            var languages = await database.ReadAsync(async connection =>
            {
                var result = new List<string>();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT language FROM official_reference_clip WHERE speaker_id=$speaker AND source_hash='shared-source' ORDER BY language";
                command.Parameters.AddWithValue("$speaker", speakerId);
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken)) result.Add(reader.GetString(0));
                return result;
            }, TestContext.Current.CancellationToken);

            Assert.Equal(["english", "und"], languages);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SameSourceCanBuildIndependentLanguagePackages()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            using var database = new Database(path);
            await database.WriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO speaker(stable_key,display_name,first_territory,created_utc)
                    VALUES('npc:language-reference','Language Reference',1,'now');
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            var registry = new VoiceRegistry(database);
            await using var builder = NewBuilder(database, registry, root);
            var speakerId = await SpeakerIdAsync(database);

            await builder.AddPcmAsync(speakerId, "shared-source", "English line", "english",
                Pcm(), TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speakerId, "shared-source", "Japanese line", "ja",
                Pcm(), TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speakerId, "english-2", "English two", "english",
                Pcm(), TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speakerId, "english-3", "English three", "english",
                Pcm(), TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speakerId, "japanese-2", "Japanese two", "japanese",
                Pcm(), TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speakerId, "japanese-3", "Japanese three", "japanese",
                Pcm(), TestContext.Current.CancellationToken);

            var languages = await database.ReadAsync(async connection =>
            {
                var result = new List<string>();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT DISTINCT p.language
                    FROM speaker_voice sv JOIN voice_profile p ON p.id=sv.profile_id
                    WHERE sv.speaker_id=$speaker ORDER BY p.language
                    """;
                command.Parameters.AddWithValue("$speaker", speakerId);
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken)) result.Add(reader.GetString(0));
                return result;
            }, TestContext.Current.CancellationToken);

            Assert.Equal(["english", "japanese"], languages);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BuiltSameLanguageOfficialReferenceSupersedesExistingDesignedAssignment()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voices.sqlite3");
        try
        {
            using var database = new Database(path);
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync("npc:official-over-designed", 20,
                "Official Over Designed", 1, "english", TestContext.Current.CancellationToken);
            var designed = await registry.SaveAndAssignAsync(speaker.Id,
                VoiceRegistry.CreateProfile(VoiceProfileKind.Designed, "english", "model", 1,
                    "designed", 1, new VoiceReference([0.2f], [2], 1, 1, "designed")),
                TestContext.Current.CancellationToken);
            StoredVoiceProfile? built = null;
            await using var builder = NewBuilder(database, registry, root);
            builder.ProfileBuilt += (_, profile) => built = profile;

            await builder.AddPcmAsync(speaker.Id, "official-source-1", "Official one", "english",
                Pcm(), TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speaker.Id, "official-source-2", "Official two", "english",
                Pcm(), TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speaker.Id, "official-source-3", "Official three", "english",
                Pcm(), TestContext.Current.CancellationToken);

            Assert.NotNull(built);
            var official = built!;
            Assert.Equal(VoiceProfileKind.Official, official.Kind);
            Assert.Equal("english", official.Language);
            Assert.NotEqual(designed.Id, official.Id);
            Assert.Equal(official.Id, (await registry.GetBestVoiceAsync(speaker.Id, "english",
                TestContext.Current.CancellationToken))?.Id);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BuiltPackageDeletesTemporaryPcmButRetainsExactGameResourceCoordinates()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync(
                "official:test", null, "Test", 0, "english", TestContext.Current.CancellationToken);
            await using var builder = NewBuilder(database, registry, root);

            for (var index = 0; index < 3; index++)
                await builder.AddPcmAsync(speaker.Id, $"source-{index}", $"Line {index}", "english", Pcm(),
                    TestContext.Current.CancellationToken, $"cut/test/voice_{index}.scd", (uint)index,
                    "curated", 7);

            var sources = await database.ReadAsync(async connection =>
            {
                var result = new List<(string Path, long Sound, string Origin, int Catalog, bool HasPcm)>();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT scd_path,sound_number,source_origin,catalog_version,pcm_path IS NOT NULL
                    FROM official_reference_clip ORDER BY sound_number
                    """;
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                    result.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3),
                        reader.GetBoolean(4)));
                return result;
            }, TestContext.Current.CancellationToken);

            Assert.Equal(3, sources.Count);
            Assert.All(sources, source =>
            {
                Assert.StartsWith("cut/test/voice_", source.Path);
                Assert.Equal("curated", source.Origin);
                Assert.Equal(7, source.Catalog);
                Assert.False(source.HasPcm);
            });
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PackageSelectionFindsValidCombinationAndIncludesBoundarySilence()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var runtime = new ReferenceRuntime();
            var speaker = await registry.ResolveSpeakerAsync(
                "official:combination", null, "Combination", 0, "english", TestContext.Current.CancellationToken);
            await using var builder = new OfficialReferenceBuilder(
                database, registry, runtime, new ScdExtractor(), root, "model");

            await builder.AddPcmAsync(speaker.Id, "seven", "Seven", "english", Pcm(7),
                TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speaker.Id, "five", "Five", "english", Pcm(5),
                TestContext.Current.CancellationToken);
            await builder.AddPcmAsync(speaker.Id, "six", "Six", "english", Pcm(6),
                TestContext.Current.CancellationToken);

            Assert.Equal(267600, runtime.LastReferenceSamples);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ObservedPackageCanPersistWithoutInferenceUntilSafeProcessing()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-deferred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var runtime = new ReferenceRuntime();
            var speaker = await registry.ResolveSpeakerAsync(
                "official:deferred", null, "Deferred", 0, "english", TestContext.Current.CancellationToken);
            await using var builder = new OfficialReferenceBuilder(
                database, registry, runtime, new ScdExtractor(), root, "model");

            for (var index = 0; index < 3; index++)
                await builder.AddPcmAsync(speaker.Id, $"deferred-{index}", $"Line {index}", "english", Pcm(),
                    TestContext.Current.CancellationToken, $"cut/test/deferred_{index}.scd", (uint)index,
                    "observed", buildProfile: false);

            Assert.Equal(0, runtime.LastReferenceSamples);
            Assert.Null(await registry.GetBestVoiceAsync(
                speaker.Id, "english", TestContext.Current.CancellationToken));

            await builder.ProcessPendingAsync("english", TestContext.Current.CancellationToken);

            Assert.True(runtime.LastReferenceSamples >= 240_000);
            Assert.Equal(VoiceProfileKind.Official, (await registry.GetBestVoiceAsync(
                speaker.Id, "english", TestContext.Current.CancellationToken))?.Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LaterObservedClipsDoNotSilentlyReplaceStableOfficialProfile()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-stable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var runtime = new ReferenceRuntime();
            var speaker = await registry.ResolveSpeakerAsync(
                "official:stable", null, "Stable", 0, "english", TestContext.Current.CancellationToken);
            await using var builder = new OfficialReferenceBuilder(
                database, registry, runtime, new ScdExtractor(), root, "model");

            for (var index = 0; index < 3; index++)
                await builder.AddPcmAsync(speaker.Id, $"first-{index}", $"First {index}", "english", Pcm(),
                    TestContext.Current.CancellationToken, buildProfile: false);
            await builder.ProcessPendingAsync("english", TestContext.Current.CancellationToken);
            var first = await registry.GetBestVoiceAsync(speaker.Id, "english", TestContext.Current.CancellationToken);

            var observedAfterStable = await builder.ObserveAsync(
                speaker.Id, "cut/test/stable-later.scd", 7, "Later observed", "english",
                TestContext.Current.CancellationToken);
            await builder.ProcessPendingAsync("english", TestContext.Current.CancellationToken);
            var consumedStatus = await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT decode_status FROM official_reference_clip
                    WHERE speaker_id=$speaker AND scd_path='cut/test/stable-later.scd'
                    """;
                command.Parameters.AddWithValue("$speaker", speaker.Id);
                return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            for (var index = 0; index < 3; index++)
                await builder.AddPcmAsync(speaker.Id, $"later-{index}", $"Later {index}", "english", Pcm(),
                    TestContext.Current.CancellationToken, buildProfile: false);
            await builder.ProcessPendingAsync("english", TestContext.Current.CancellationToken);
            var later = await registry.GetBestVoiceAsync(speaker.Id, "english", TestContext.Current.CancellationToken);
            var pendingPcm = await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM official_reference_clip WHERE speaker_id=$speaker AND language='english' AND pcm_path IS NOT NULL";
                command.Parameters.AddWithValue("$speaker", speaker.Id);
                return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }, TestContext.Current.CancellationToken);

            Assert.NotNull(first);
            Assert.Equal(VoiceProfileKind.Official, first!.Kind);
            Assert.Equal(OfficialReferenceObservationStatus.Pending, observedAfterStable.Status);
            Assert.Equal("consumed", consumedStatus);
            Assert.Equal(first.Id, later?.Id);
            Assert.Equal(0, pendingPcm);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DuplicateSourceIsReportedWithoutAddingAnotherClip()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync(
                "official:duplicate", null, "Duplicate", 0, "english", TestContext.Current.CancellationToken);
            await using var builder = NewBuilder(database, registry, root);

            var stored = await builder.AddPcmAsync(speaker.Id, "same-source", "Line", "english", Pcm(),
                TestContext.Current.CancellationToken, "cut/test/duplicate.scd", 1, "observed",
                buildProfile: false);
            var duplicate = await builder.AddPcmAsync(speaker.Id, "same-source", "Line", "english", Pcm(),
                TestContext.Current.CancellationToken, "cut/test/duplicate.scd", 1, "observed",
                buildProfile: false);
            var count = await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM official_reference_clip WHERE speaker_id=$speaker";
                command.Parameters.AddWithValue("$speaker", speaker.Id);
                return Convert.ToInt32(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            }, TestContext.Current.CancellationToken);

            Assert.True(stored);
            Assert.False(duplicate);
            Assert.Equal(1, count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ObservationPersistsSourceMetadataBeforeSafeIdleDecode()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-reference-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "voices.sqlite3"));
            var registry = new VoiceRegistry(database);
            var speaker = await registry.ResolveSpeakerAsync("official:observed", null, "Observed", 0,
                "english", TestContext.Current.CancellationToken);
            await using var builder = NewBuilder(database, registry, root);

            var result = await builder.ObserveAsync(speaker.Id, "cut/test/observed.scd", 42,
                "Observed line", "english", TestContext.Current.CancellationToken);
            var row = await database.ReadAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT scd_path,sound_number,transcript,pcm_path,decode_status
                    FROM official_reference_clip WHERE speaker_id=$speaker
                    """;
                command.Parameters.AddWithValue("$speaker", speaker.Id);
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
                return (reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4));
            }, TestContext.Current.CancellationToken);

            Assert.Equal(OfficialReferenceObservationStatus.Pending, result.Status);
            Assert.Equal("cut/test/observed.scd", row.Item1);
            Assert.Equal(42, row.Item2);
            Assert.Equal("Observed line", row.Item3);
            Assert.Null(row.Item4);
            Assert.Equal("pending", row.Item5);
        }
        finally { Directory.Delete(root, true); }
    }

    private static OfficialReferenceBuilder NewBuilder(Database database, VoiceRegistry registry, string root) =>
        new(database, registry, new ReferenceRuntime(), new ScdExtractor(), root, "model");

    private static float[] Pcm() => Enumerable.Repeat(0.1f, 79200).ToArray();
    private static float[] Pcm(double seconds) => Enumerable.Repeat(0.1f, (int)(24000 * seconds)).ToArray();

    private static Task<long> SpeakerIdAsync(Database database) => database.ReadAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM speaker ORDER BY id LIMIT 1";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }, TestContext.Current.CancellationToken);

    private sealed class ReferenceRuntime : ITtsRuntime
    {
        public int LastReferenceSamples { get; private set; }
        public RuntimeCapabilities Capabilities { get; } = new(false, false, false, true, []);

        public ValueTask<VoiceReference> ExtractReferenceAsync(ReadOnlyMemory<float> monoPcm24Khz,
            string transcript, CancellationToken token)
        {
            LastReferenceSamples = monoPcm24Khz.Length;
            return ValueTask.FromResult(new VoiceReference([0.1f], [1], 1, 1, transcript));
        }

        public Task SynthesizeAsync(SynthesisRequest request, StreamingAudioBuffer sink, CancellationToken token) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
