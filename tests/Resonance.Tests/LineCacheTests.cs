using Resonance.Audio;
using Resonance.Data;
using Resonance.Scheduling;

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
