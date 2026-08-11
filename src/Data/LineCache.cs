using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Data.Sqlite;
using Resonance.Audio;
using Resonance.Scheduling;

namespace Resonance.Data;

public sealed class LineCache
{
    private static readonly TimeSpan UncertainOwnerGrace = TimeSpan.FromHours(1);
    private readonly Database database;
    private readonly string directory;
    private readonly string cleanupLeasePath;
    private readonly Func<long> limitBytes;
    private readonly Func<SqliteConnection, CancellationToken, Task>? beforeDatabaseCommit;
    private readonly object startupCleanupGate = new();
    private Task? startupCleanup;

    public event Action<Exception>? Failed;

    public LineCache(Database database, string directory, Func<long> limitBytes,
        Func<SqliteConnection, CancellationToken, Task>? beforeDatabaseCommit = null)
    {
        this.database = database;
        this.directory = directory;
        cleanupLeasePath = Path.Combine(directory, ".cleanup.lock");
        this.limitBytes = limitBytes;
        this.beforeDatabaseCommit = beforeDatabaseCommit;
        Directory.CreateDirectory(directory);
    }

    public async Task<bool> TryPopulateAsync(
        DubLine line, string profileHash, string modelHash, string language, long seed, CancellationToken token)
    {
        await EnsureStartupCleanupAsync(token).ConfigureAwait(false);
        using var readLease = TryAcquireCleanupLease();
        if (readLease is null) return false;
        var key = CacheKey(profileHash, modelHash, language, line.Text, seed);
        string? path = null;
        string? observedVersion = null;
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
            if (!IsCachePath(path) || !IsSafeRegularFile(path))
                throw new InvalidDataException("Cached PCM path is outside the cache root or is not a regular file");
            observedVersion = GetFileVersion(path);
            var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            if (observedVersion is not null
                && !String.Equals(GetFileVersion(path), observedVersion, StringComparison.Ordinal))
                return false;
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0) throw new InvalidDataException("Invalid cached PCM length");
            var samples = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return line.TryReplaceAudioAndTransition(
                StreamingAudioBuffer.FromSamples(samples),
                DubLineState.Buffered,
                DubLineState.VoiceResolving,
                DubLineState.Queued,
                DubLineState.Predicted,
                DubLineState.Generating,
                DubLineState.Buffered);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            await DeleteEntryAsync(key, path!, observedVersion, token).ConfigureAwait(false);
            Failed?.Invoke(error);
            return false;
        }
    }

    public async Task StoreAsync(
        DubLine line, string profileId, string profileHash, string modelHash, string language, long seed,
        float[] samples, CancellationToken token)
    {
        await EnsureStartupCleanupAsync(token).ConfigureAwait(false);
        if (samples.Length == 0) return;
        if (limitBytes() <= 0)
        {
            await EnforceLimitAsync(token).ConfigureAwait(false);
            return;
        }

        string? path = null;
        string? temporary = null;
        string? backup = null;
        string? ownerPath = null;
        var committed = false;
        var moved = false;
        var backupCreated = false;
        FileStream? writerLease = null;
        try
        {
            var key = CacheKey(profileHash, modelHash, language, line.Text, seed);
            var operationNonce = Guid.NewGuid().ToString("N");
            path = Path.Combine(directory, key + ".f32");
            ownerPath = Path.Combine(directory, $".{operationNonce}.owner.json");
            writerLease = TryAcquireCleanupLease();
            if (writerLease is null) return;
            PublishOwner(ownerPath, operationNonce);
            temporary = path + $".{operationNonce}.part";
            backup = path + $".{operationNonce}.previous";
            var bytes = new byte[checked(samples.Length * sizeof(float))];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            if (File.Exists(path))
            {
                File.Copy(path, backup, true);
                backupCreated = true;
            }
            File.Move(temporary, path, true);
            moved = true;
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
                if (beforeDatabaseCommit is not null)
                    await beforeDatabaseCommit(connection, token).ConfigureAwait(false);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            committed = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SqliteException)
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            if (!committed)
            {
                if (moved && path is not null)
                {
                    try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                    if (backupCreated && backup is not null)
                        try { File.Move(backup, path, true); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                }
                else if (backupCreated && backup is not null)
                    try { File.Delete(backup); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
            if (committed && backup is not null)
                try { File.Delete(backup); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            Failed?.Invoke(error);
        }
        catch
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            if (!committed)
            {
                if (moved && path is not null)
                {
                    try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                    if (backupCreated && backup is not null)
                        try { File.Move(backup, path, true); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                }
                else if (backupCreated && backup is not null)
                    try { File.Delete(backup); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
            if (committed && backup is not null)
                try { File.Delete(backup); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
        finally
        {
            if (committed && backup is not null)
                try { File.Delete(backup); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            if (ownerPath is not null)
            {
                try { File.Delete(ownerPath); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                try { File.Delete(ownerPath + ".pending"); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
            writerLease?.Dispose();
        }
        if (committed) await EnforceLimitAsync(token).ConfigureAwait(false);
    }

    private async Task EnforceLimitAsync(CancellationToken token)
    {
        using var cleanupLease = TryAcquireCleanupLease();
        if (cleanupLease is null) return;
        // Another live writer may have moved a final file before its DB
        // transaction commits.  Do not evict rows/files across that lease.
        if (HasLiveOwner()) return;
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
        {
            if (!CanDeleteTransient(entry.Path)) continue;
            try { File.Delete(entry.Path); } catch (IOException error) { Failed?.Invoke(error); }
        }
    }

    private async Task EnsureStartupCleanupAsync(CancellationToken token)
    {
        Task cleanup;
        lock (startupCleanupGate)
            cleanup = startupCleanup ??= CleanupStartupAsync();
        await cleanup.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task CleanupStartupAsync()
    {
        using var cleanupLease = TryAcquireCleanupLease();
        if (cleanupLease is null) return;
        var referenced = await database.ReadAsync(async connection =>
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT audio_path FROM line_cache";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None).ConfigureAwait(false);
            while (await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var value = reader.GetString(0);
                try { result.Add(Path.GetFullPath(value)); }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
            return result;
        }, CancellationToken.None).ConfigureAwait(false);

        var liveOwner = false;
        var ownerPaths = Directory.EnumerateFiles(directory, "*.owner.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "*.owner.json.pending", SearchOption.TopDirectoryOnly))
            .Where(IsSafeRegularFile)
            .ToArray();
        foreach (var ownerPath in ownerPaths)
        {
            if (ownerPath.EndsWith(".owner.json.pending", StringComparison.OrdinalIgnoreCase))
            {
                // A crash between writing the owner record and publishing it
                // leaves an uncertain live lease.  Only an aged record with a
                // verified-dead owner may be reaped; all other records retain
                // their transient files.
                if (!TryReapUncertainOwner(ownerPath)) liveOwner = true;
                continue;
            }
            try
            {
                if (!IsSafeRegularFile(ownerPath)) { liveOwner = true; continue; }
                var owner = JsonSerializer.Deserialize<CacheOwner>(await File.ReadAllTextAsync(ownerPath)
                    .ConfigureAwait(false));
                if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                    || String.IsNullOrWhiteSpace(owner.InstanceNonce))
                {
                    liveOwner = true;
                    continue;
                }
                if (IsOwnerAlive(owner)) liveOwner = true;
                else if (CanDeleteTransient(ownerPath)) File.Delete(ownerPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or JsonException or InvalidOperationException or Win32Exception)
            {
                // Unknown ownership is treated as live.  Cleanup must not
                // remove a transient file belonging to an overlapping reload.
                liveOwner = true;
            }
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            if (!IsSafeRegularFile(path)) continue;
            var name = Path.GetFileName(path);
            var candidate = name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".previous", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".f32", StringComparison.OrdinalIgnoreCase);
            if (!candidate) continue;
            if (liveOwner) continue;
            var fullPath = Path.GetFullPath(path);
            if (referenced.Contains(fullPath)) continue;
            if (!CanDeleteTransient(fullPath)) continue;
            try { File.Delete(fullPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Failed?.Invoke(error);
            }
        }
    }

    private static void PublishOwner(string ownerPath, string operationNonce)
    {
        using var process = Process.GetCurrentProcess();
        var owner = new CacheOwner(process.Id, process.StartTime.ToUniversalTime().Ticks,
            operationNonce);
        var temporary = ownerPath + ".pending";
        File.WriteAllText(temporary, JsonSerializer.Serialize(owner));
        File.Move(temporary, ownerPath, true);
    }

    private bool HasLiveOwner()
    {
        var ownerPaths = Directory.EnumerateFiles(directory, "*.owner.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "*.owner.json.pending", SearchOption.TopDirectoryOnly))
            .Where(IsSafeRegularFile)
            .ToArray();
        foreach (var ownerPath in ownerPaths)
        {
            if (ownerPath.EndsWith(".owner.json.pending", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReapUncertainOwner(ownerPath)) return true;
                continue;
            }
            try
            {
                if (!IsSafeRegularFile(ownerPath)) return true;
                var owner = JsonSerializer.Deserialize<CacheOwner>(File.ReadAllText(ownerPath));
                if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                    || String.IsNullOrWhiteSpace(owner.InstanceNonce)
                    || IsOwnerAlive(owner)) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or JsonException or InvalidOperationException or Win32Exception)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsOwnerAlive(CacheOwner owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == owner.ProcessStartUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private FileStream? TryAcquireCleanupLease()
    {
        try
        {
            if (!IsSafeDirectory(directory)) return null;
            if (File.Exists(cleanupLeasePath)
                && File.GetAttributes(cleanupLeasePath).HasFlag(FileAttributes.ReparsePoint)) return null;
            return new FileStream(cleanupLeasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.SequentialScan);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private bool TryReapUncertainOwner(string ownerPath)
    {
        if (!IsSafeRegularFile(ownerPath)) return false;
        try
        {
            if (!IsSafeRegularFile(ownerPath)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(ownerPath) < UncertainOwnerGrace) return false;
            if (!IsSafeRegularFile(ownerPath)) return false;
            var owner = JsonSerializer.Deserialize<CacheOwner>(File.ReadAllText(ownerPath));
            if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                || String.IsNullOrWhiteSpace(owner.InstanceNonce) || IsOwnerAlive(owner)) return false;
            if (!IsSafeRegularFile(ownerPath)) return false;
            File.Delete(ownerPath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or JsonException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private bool CanDeleteTransient(string path)
    {
        if (!IsCachePath(path) || !IsSafeRegularFile(path) || HasLiveOwner()) return false;
        return IsSafeRegularFile(path);
    }

    private bool IsCachePath(string path)
    {
        try
        {
            if (!IsSafeDirectory(directory)) return false;
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSafeRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.ReparsePoint)
                && !attributes.HasFlag(FileAttributes.Directory);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task DeleteEntryAsync(string key, string path, string? observedVersion, CancellationToken token)
    {
        if (observedVersion is not null && !String.Equals(GetFileVersion(path), observedVersion, StringComparison.Ordinal))
            return;
        var deleted = false;
        await database.WriteAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM line_cache WHERE cache_key=$key AND audio_path=$path";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$path", path);
            deleted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, token).ConfigureAwait(false);
        if (!deleted) return;
        if (observedVersion is not null && !String.Equals(GetFileVersion(path), observedVersion, StringComparison.Ordinal))
            return;
        if (!IsCachePath(path) || !IsSafeRegularFile(path)) return;
        try { File.Delete(path); } catch (IOException error) { Failed?.Invoke(error); }
    }

    private static string? GetFileVersion(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}:{info.CreationTimeUtc.Ticks}";
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static string CacheKey(string profileHash, string modelHash, string language, string text, long seed) =>
        HexHash($"v1\n{modelHash}\n{profileHash}\n{language}\n{Normalize(text)}\nseed={seed}\nmax=2048");

    private sealed record CacheOwner(int ProcessId, long ProcessStartUtcTicks, string InstanceNonce);

    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string HexHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
