using Resonance.Audio;
using Resonance.Data;
using Resonance.Scheduling;
using Directory = Resonance.Tests.TestDirectory;

namespace Resonance.Tests;

public sealed class LineCacheTests
{
    [Fact]
    public async Task StoresLoadsAndEvictsCompletedPcm()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-line-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            await InsertProfile(database, "profile-1", "profile-hash", TestContext.Current.CancellationToken);
            long limit = 1024;
            var cache = new LineCache(database, Path.Combine(root, "audio"), () => limit);
            using var generated = Line("hello");
            generated.Audio.TryWrite([.25f, -.5f, 1f]);
            generated.Audio.Complete();

            await cache.StoreAsync(generated, "profile-1", "profile-hash", "model-hash", "english", 7,
                [.25f, -.5f, 1f], TestContext.Current.CancellationToken);
            var generatedSamples = await generated.Audio.DrainAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new[] { .25f, -.5f, 1f }, generatedSamples);

            using var hit = Line("hello");
            Assert.True(await cache.TryPopulateAsync(hit, "profile-hash", "model-hash", "english", 7,
                TestContext.Current.CancellationToken));
            var hitSamples = await hit.Audio.DrainAsync(TestContext.Current.CancellationToken);
            Assert.Equal(new[] { .25f, -.5f, 1f }, hitSamples);

            limit = 0;
            using var eviction = Line("different");
            eviction.Audio.TryWrite([.1f]);
            eviction.Audio.Complete();
            await cache.StoreAsync(eviction, "profile-1", "profile-hash", "model-hash", "english", 7,
                [.1f], TestContext.Current.CancellationToken);
            using var miss = Line("hello");
            Assert.False(await cache.TryPopulateAsync(miss, "profile-hash", "model-hash", "english", 7,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FailedDatabaseCommitRemovesCacheArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-line-cache-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            var audioDirectory = Path.Combine(root, "audio");
            var cache = new LineCache(database, audioDirectory, () => 1024,
                async (connection, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT * FROM intentionally_missing_line_cache_table";
                    try { await command.ExecuteNonQueryAsync(token); }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException("injected database commit failure", error);
                    }
                });
            using var line = Line("commit failure");

            await Assert.ThrowsAsync<InvalidOperationException>(() => cache.StoreAsync(
                line, "profile-1", "profile-hash", "model-hash", "english", 7,
                [.25f, -.5f, 1f], TestContext.Current.CancellationToken));

            var artifacts = System.IO.Directory.EnumerateFileSystemEntries(audioDirectory)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .ToArray();
            Assert.DoesNotContain(artifacts, name => name!.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(artifacts, name => name!.EndsWith(".previous", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(artifacts, name => name!.EndsWith(".owner.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(artifacts, name => name!.EndsWith(".owner.json.pending", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(artifacts, name => name!.EndsWith(".response.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(artifacts, name => name!.EndsWith(".f32", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task StartupCleanupRemovesOnlyUnreferencedCacheArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "resonance-line-cache-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new Database(Path.Combine(root, "test.sqlite3"));
            await InsertProfile(database, "profile-1", "profile-hash", TestContext.Current.CancellationToken);
            var audioDirectory = Path.Combine(root, "audio");
            Directory.CreateDirectory(audioDirectory);
            var referenced = Path.Combine(audioDirectory, "referenced.f32");
            await File.WriteAllBytesAsync(referenced, new byte[] { 0, 0, 0, 0 }, TestContext.Current.CancellationToken);
            var orphan = Path.Combine(audioDirectory, "orphan.f32");
            var partial = Path.Combine(audioDirectory, "orphan.f32.part");
            var previous = Path.Combine(audioDirectory, "orphan.f32.previous");
            await File.WriteAllBytesAsync(orphan, new byte[] { 0, 0, 0, 0 }, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(partial, new byte[] { 0, 0, 0, 0 }, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(previous, new byte[] { 0, 0, 0, 0 }, TestContext.Current.CancellationToken);
            await database.WriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO line_cache(cache_key,profile_id,normalized_text_hash,model_hash,audio_path,duration,bytes,last_used_utc)
                    VALUES('referenced','profile-1','text','model',$path,1,4,$utc)
                    """;
                command.Parameters.AddWithValue("$path", referenced);
                command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }, TestContext.Current.CancellationToken);

            var cache = new LineCache(database, audioDirectory, () => 1024);
            using var line = Line("startup");
            Assert.False(await cache.TryPopulateAsync(line, "profile-hash", "model-hash", "english", 7,
                TestContext.Current.CancellationToken));
            Assert.True(File.Exists(referenced));
            Assert.False(File.Exists(orphan));
            Assert.False(File.Exists(partial));
            Assert.False(File.Exists(previous));
        }
        finally { Directory.Delete(root, true); }
    }

    private static DubLine Line(string text) => new()
    {
        SessionEpoch = 1,
        Sequence = 1,
        SpeakerKey = "npc:1",
        SpeakerName = "Test",
        Text = text,
    };

    private static Task InsertProfile(Database database, string id, string hash, CancellationToken token) =>
        database.WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO voice_profile(id,kind,language,model_hash,ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks,profile_hash,created_utc)
                VALUES($id,1,'english','model','text',X'00000000',X'00000000',1,1,$hash,$utc)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(token);
        }, token);
}
