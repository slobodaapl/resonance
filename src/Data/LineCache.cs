using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Resonance.Audio;
using Resonance.Scheduling;

namespace Resonance.Data;

public sealed class LineCache
{
    private readonly Database database;
    private readonly string directory;
    private readonly Func<long> limitBytes;

    public event Action<Exception>? Failed;

    public LineCache(Database database, string directory, Func<long> limitBytes)
    {
        this.database = database;
        this.directory = directory;
        this.limitBytes = limitBytes;
        Directory.CreateDirectory(directory);
    }

    public async Task<bool> TryPopulateAsync(
        DubLine line, string profileHash, string modelHash, string language, long seed, CancellationToken token)
    {
        var key = CacheKey(profileHash, modelHash, language, line.Text, seed);
        string? path = null;
        await database.WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT audio_path FROM line_cache WHERE cache_key=$key";
            command.Parameters.AddWithValue("$key", key);
            path = (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (path is null) return;
            command.CommandText = "UPDATE line_cache SET last_used_utc=$used WHERE cache_key=$key";
            command.Parameters.AddWithValue("$used", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        if (path is null) return false;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) throw new InvalidDataException("Invalid cached PCM length");
            var samples = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            line.ReplaceAudio(StreamingAudioBuffer.FromSamples(samples));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await DeleteEntryAsync(key, path, token).ConfigureAwait(false);
            Failed?.Invoke(error);
            return false;
        }
    }

    public async Task StoreAsync(
        DubLine line, string profileId, string profileHash, string modelHash, string language, long seed,
        float[] samples, CancellationToken token)
    {
        if (samples.Length == 0) return;
        if (limitBytes() <= 0)
        {
            await EnforceLimitAsync(token).ConfigureAwait(false);
            return;
        }

        try
        {
            var key = CacheKey(profileHash, modelHash, language, line.Text, seed);
            var path = Path.Combine(directory, key + ".f32");
            var temporary = path + ".part";
            var bytes = new byte[checked(samples.Length * sizeof(float))];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
            var textHash = HexHash(Normalize(line.Text));
            await database.WriteAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO line_cache(cache_key,profile_id,normalized_text_hash,model_hash,audio_path,duration,bytes,last_used_utc)
                    VALUES($key,$profile,$text,$model,$path,$duration,$bytes,$used)
                    ON CONFLICT(cache_key) DO UPDATE SET
                      audio_path=excluded.audio_path,duration=excluded.duration,bytes=excluded.bytes,last_used_utc=excluded.last_used_utc
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$profile", profileId);
                command.Parameters.AddWithValue("$text", textHash);
                command.Parameters.AddWithValue("$model", modelHash);
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$duration", samples.Length / 24000d);
                command.Parameters.AddWithValue("$bytes", bytes.LongLength);
                command.Parameters.AddWithValue("$used", DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            await EnforceLimitAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            Failed?.Invoke(error);
        }
    }

    private async Task EnforceLimitAsync(CancellationToken token)
    {
        var remove = new List<(string Key, string Path)>();
        await database.WriteAsync(async connection =>
        {
            await using var total = connection.CreateCommand();
            total.CommandText = "SELECT COALESCE(SUM(bytes),0) FROM line_cache";
            var bytes = Convert.ToInt64(await total.ExecuteScalarAsync(token).ConfigureAwait(false));
            var limit = Math.Max(0, limitBytes());
            if (bytes <= limit) return;
            await using var oldest = connection.CreateCommand();
            oldest.CommandText = "SELECT cache_key,audio_path,bytes FROM line_cache ORDER BY last_used_utc ASC";
            await using var reader = await oldest.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (bytes > limit && await reader.ReadAsync(token).ConfigureAwait(false))
            {
                remove.Add((reader.GetString(0), reader.GetString(1)));
                bytes -= reader.GetInt64(2);
            }
            await reader.DisposeAsync().ConfigureAwait(false);
            foreach (var entry in remove)
            {
                await using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM line_cache WHERE cache_key=$key";
                delete.Parameters.AddWithValue("$key", entry.Key);
                await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
        foreach (var entry in remove)
            try { File.Delete(entry.Path); } catch (IOException error) { Failed?.Invoke(error); }
    }

    private async Task DeleteEntryAsync(string key, string path, CancellationToken token)
    {
        await database.WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM line_cache WHERE cache_key=$key";
            command.Parameters.AddWithValue("$key", key);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        try { File.Delete(path); } catch (IOException error) { Failed?.Invoke(error); }
    }

    private static string CacheKey(string profileHash, string modelHash, string language, string text, long seed) =>
        HexHash($"v1\n{modelHash}\n{profileHash}\n{language}\n{Normalize(text)}\nseed={seed}\nmax=2048");

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string HexHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
