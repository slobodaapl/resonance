using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Data;
using Resonance.Tts;

namespace Resonance.Game;

public sealed class OfficialReferenceBuilder : IAsyncDisposable
{
    public const double RequiredSeconds = 10.0;
    private const double MaximumPackageSeconds = 12.0;
    private const int BoundarySilenceSamples = 3600;
    private readonly Database database;
    private readonly VoiceRegistry voices;
    private readonly ITtsRuntime runtime;
    private readonly ScdExtractor extractor;
    private readonly string directory;
    private readonly string modelHash;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();

    public event Action<long, StoredVoiceProfile>? ProfileBuilt;

    public OfficialReferenceBuilder(Database database, VoiceRegistry voices, ITtsRuntime runtime,
        ScdExtractor extractor, string directory, string modelHash)
    {
        this.database = database;
        this.voices = voices;
        this.runtime = runtime;
        this.extractor = extractor;
        this.directory = directory;
        this.modelHash = modelHash;
        Directory.CreateDirectory(directory);
    }

    public async Task ObserveAsync(long speakerId, string scdPath, uint soundNumber, string transcript,
        string language, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        token = linked.Token;
        language = NormalizeLanguage(language);
        var sourceHash = Hash($"{scdPath.ToLowerInvariant()}\n{soundNumber}");
        if (await PrepareSourceAsync(speakerId, sourceHash, language, token).ConfigureAwait(false)) return;
        var pcm = await extractor.ExtractMono24KhzAsync(scdPath, soundNumber, token).ConfigureAwait(false);
        await AddPcmAsync(speakerId, sourceHash, transcript, language, pcm, token,
            scdPath, soundNumber, "observed", null).ConfigureAwait(false);
    }

    public async Task AddCuratedAsync(long speakerId, OfficialVoiceSource source, string language,
        int catalogVersion, CancellationToken token)
    {
        var sourceHash = Hash($"{source.ScdPath.ToLowerInvariant()}\n{source.SoundNumber}");
        var pcm = await extractor.ExtractMono24KhzAsync(source.ScdPath, source.SoundNumber, token).ConfigureAwait(false);
        await AddPcmAsync(speakerId, sourceHash, source.Transcript, language, pcm, token,
            source.ScdPath, source.SoundNumber, "curated", catalogVersion, restoreExisting: true,
            sourcePriority: source.Preferred ? 100 : 50).ConfigureAwait(false);
    }

    public async Task ProcessPendingAsync(string language, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        token = linked.Token;
        language = NormalizeLanguage(language);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await CleanupOrphanPcmAsync(token).ConfigureAwait(false);
            var pending = await database.ReadAsync(async connection =>
            {
                var result = new List<(long SpeakerId, string Language)>();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT speaker_id,language FROM official_reference_clip
                    WHERE pcm_path IS NOT NULL AND language=$language
                    GROUP BY speaker_id,language
                    """;
                command.Parameters.AddWithValue("$language", language);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                    result.Add((reader.GetInt64(0), reader.GetString(1)));
                return result;
            }, token).ConfigureAwait(false);
            foreach (var package in pending)
                await TryBuildAsync(package.SpeakerId, package.Language, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<StoredVoiceProfile?> RebuildPersistedAsync(
        long speakerId,
        string language,
        CancellationToken token)
    {
        language = NormalizeLanguage(language);
        var sources = await database.ReadAsync(async connection =>
        {
            var result = new List<(string Hash, string Path, uint Sound, string Transcript, string Origin,
                int Priority, int? Catalog)>();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT source_hash,scd_path,sound_number,transcript,source_origin,source_priority,catalog_version
                FROM official_reference_clip
                WHERE speaker_id=$speaker AND language=$language
                  AND scd_path IS NOT NULL AND sound_number IS NOT NULL
                ORDER BY source_priority DESC,created_utc,id
                """;
            command.Parameters.AddWithValue("$speaker", speakerId);
            command.Parameters.AddWithValue("$language", language);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                result.Add((reader.GetString(0), reader.GetString(1), checked((uint)reader.GetInt64(2)),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            return result;
        }, token).ConfigureAwait(false);
        foreach (var source in sources)
        {
            try
            {
                var pcm = await extractor.ExtractMono24KhzAsync(source.Path, source.Sound, token).ConfigureAwait(false);
                await AddPcmCoreAsync(speakerId, source.Hash, source.Transcript, language, pcm, token,
                    source.Path, source.Sound, source.Origin, source.Catalog, true, source.Priority).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                continue;
            }
            var profile = await voices.GetBestVoiceAsync(speakerId, language, token).ConfigureAwait(false);
            if (profile is { Kind: VoiceProfileKind.Official }
                && String.Equals(profile.ModelHash, modelHash, StringComparison.Ordinal)) return profile;
        }
        return null;
    }

    internal async Task AddPcmAsync(long speakerId, string sourceHash, string transcript, string language,
        float[] pcm, CancellationToken token, string? scdPath = null, uint? soundNumber = null,
        string sourceOrigin = "legacy", int? catalogVersion = null, bool restoreExisting = false,
        int sourcePriority = 0)
        => await AddPcmCoreAsync(speakerId, sourceHash, transcript, language, pcm, token, scdPath,
            soundNumber, sourceOrigin, catalogVersion, restoreExisting, sourcePriority).ConfigureAwait(false);

    private async Task AddPcmCoreAsync(long speakerId, string sourceHash, string transcript, string language,
        float[] pcm, CancellationToken token, string? scdPath, uint? soundNumber,
        string sourceOrigin, int? catalogVersion, bool restoreExisting, int sourcePriority)
    {
        language = NormalizeLanguage(language);
        if (pcm.Length < 24000 / 3 || pcm.Length > 24000 * MaximumPackageSeconds
            || pcm.Any(sample => !float.IsFinite(sample)))
            throw new InvalidDataException("Official reference clip has invalid duration or samples");
        var peak = pcm.Max(sample => Math.Abs(sample));
        if (peak < 0.002f) throw new InvalidDataException("Official reference clip is effectively silent");

        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var exists = await PrepareSourceAsync(speakerId, sourceHash, language, token).ConfigureAwait(false);
            if (exists && !restoreExisting) return;
            var path = Path.Combine(directory, $"{sourceHash}.{language}.f32");
            var temporary = path + ".part";
            var bytes = new byte[checked(pcm.Length * sizeof(float))];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
            try
            {
                await database.WriteAsync(async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = exists ? """
                        UPDATE official_reference_clip
                        SET transcript=$text,pcm_path=$path,duration_seconds=$duration,
                            scd_path=$scd,sound_number=$sound,source_origin=$origin,
                            source_priority=$priority,catalog_version=$catalog,validated_utc=$validated
                        WHERE speaker_id=$speaker AND source_hash=$source AND language=$language
                        """ : """
                        INSERT OR IGNORE INTO official_reference_clip(
                          speaker_id,source_hash,language,transcript,pcm_path,duration_seconds,
                          scd_path,sound_number,source_origin,source_priority,catalog_version,validated_utc,created_utc)
                        VALUES($speaker,$source,$language,$text,$path,$duration,
                          $scd,$sound,$origin,$priority,$catalog,$validated,$utc)
                        """;
                    command.Parameters.AddWithValue("$speaker", speakerId);
                    command.Parameters.AddWithValue("$source", sourceHash);
                    command.Parameters.AddWithValue("$language", language);
                    command.Parameters.AddWithValue("$text", transcript);
                    command.Parameters.AddWithValue("$path", path);
                    command.Parameters.AddWithValue("$duration", pcm.Length / 24000d);
                    command.Parameters.AddWithValue("$scd", (object?)scdPath ?? DBNull.Value);
                    command.Parameters.AddWithValue("$sound", soundNumber is { } number ? number : DBNull.Value);
                    command.Parameters.AddWithValue("$origin", sourceOrigin);
                    command.Parameters.AddWithValue("$priority", sourcePriority);
                    command.Parameters.AddWithValue("$catalog", (object?)catalogVersion ?? DBNull.Value);
                    command.Parameters.AddWithValue("$validated", DateTimeOffset.UtcNow.ToString("O"));
                    command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }, token).ConfigureAwait(false);
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { }
                throw;
            }

            await TryBuildAsync(speakerId, language, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task TryBuildAsync(long speakerId, string language, CancellationToken token)
    {
        language = NormalizeLanguage(language);
        var clips = await LoadPackageAsync(speakerId, language, token).ConfigureAwait(false);
        if (clips.Count == 0) return;
        var samples = new List<float>();
        var transcripts = new List<string>();
        var sources = new List<string>();
        foreach (var clip in clips)
        {
            if (samples.Count > 0) samples.AddRange(new float[BoundarySilenceSamples]);
            var clipBytes = await File.ReadAllBytesAsync(clip.Path, token).ConfigureAwait(false);
            if (clipBytes.Length == 0 || clipBytes.Length % sizeof(float) != 0)
                throw new InvalidDataException("Temporary official PCM is corrupt");
            var clipPcm = new float[clipBytes.Length / sizeof(float)];
            Buffer.BlockCopy(clipBytes, 0, clipPcm, 0, clipBytes.Length);
            samples.AddRange(clipPcm);
            transcripts.Add(clip.Transcript);
            sources.Add(clip.ScdPath is null
                ? clip.SourceHash
                : $"{clip.ScdPath}#{clip.SoundNumber}");
        }
        var referenceText = string.Join(' ', transcripts);
        var reference = await runtime.ExtractReferenceAsync(samples.ToArray(), referenceText, token).ConfigureAwait(false);
        var metadata = JsonSerializer.Serialize(new { sources, durationSeconds = samples.Count / 24000d });
        var packageLanguage = clips[0].Language;
        var profile = VoiceRegistry.CreateProfile(
            VoiceProfileKind.Official, packageLanguage, modelHash, null, null, null, reference, metadata);
        profile = await voices.SaveAndAssignAsync(speakerId, profile, token).ConfigureAwait(false);
        await ForgetPcmAsync(clips, token).ConfigureAwait(false);
        ProfileBuilt?.Invoke(speakerId, profile);
    }

    private Task<bool> PrepareSourceAsync(long speakerId, string sourceHash, string language, CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var transaction = connection.BeginTransaction();
        await using var adopt = connection.CreateCommand();
        adopt.Transaction = transaction;
        adopt.CommandText = """
            UPDATE official_reference_clip
            SET language=$language
            WHERE speaker_id=$speaker AND source_hash=$source AND language='und'
              AND NOT EXISTS(
                SELECT 1 FROM official_reference_clip
                WHERE speaker_id=$speaker AND source_hash=$source AND language=$language)
            """;
        adopt.Parameters.AddWithValue("$speaker", speakerId);
        adopt.Parameters.AddWithValue("$source", sourceHash);
        adopt.Parameters.AddWithValue("$language", language);
        await adopt.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var exists = await SourceExistsAsync(connection, transaction, speakerId, sourceHash, language, token)
            .ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return exists;
    }, token);

    private static async Task<bool> SourceExistsAsync(SqliteConnection connection, SqliteTransaction transaction,
        long speakerId, string sourceHash, string language, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1 FROM official_reference_clip
              WHERE speaker_id=$speaker AND source_hash=$source AND language=$language)
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$source", sourceHash);
        command.Parameters.AddWithValue("$language", language);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
    }

    private Task<List<Clip>> LoadPackageAsync(long speakerId, string language, CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,source_hash,language,transcript,pcm_path,duration_seconds,
                   scd_path,sound_number,source_origin,source_priority
            FROM official_reference_clip
            WHERE speaker_id=$speaker AND language=$language AND pcm_path IS NOT NULL
            ORDER BY created_utc,id
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$language", language);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var candidates = new List<Clip>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var clip = new Clip(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : checked((uint)reader.GetInt64(7)), reader.GetString(8), reader.GetInt32(9));
            if (clip.Duration <= MaximumPackageSeconds) candidates.Add(clip);
        }
        var single = candidates
            .Where(clip => clip.Duration >= RequiredSeconds)
            .OrderByDescending(clip => clip.Priority)
            .ThenBy(clip => Math.Abs(clip.Duration - RequiredSeconds))
            .ThenBy(clip => clip.Id)
            .FirstOrDefault();
        if (single is not null) return [single];
        var states = new Dictionary<int, List<Clip>> { [0] = [] };
        foreach (var clip in candidates.OrderBy(value => value.Id))
        {
            foreach (var package in states.Values.ToArray())
            {
                var next = package.Append(clip).ToList();
                var seconds = next.Sum(value => value.Duration)
                              + (next.Count - 1) * BoundarySilenceSamples / 24000d;
                if (seconds > MaximumPackageSeconds) continue;
                var bucket = (int)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
                if (!states.TryGetValue(bucket, out var existing) || BetterSameDuration(next, existing))
                    states[bucket] = next;
            }
        }
        return states.Values
                   .Where(package => PackageDuration(package) >= RequiredSeconds)
                   .OrderBy(package => package.Count(value => value.Origin != "curated"))
                   .ThenByDescending(package => package.Any(value => value.Priority >= 100))
                   .ThenBy(package => package.Count)
                   .ThenBy(package => Math.Abs(PackageDuration(package) - RequiredSeconds))
                   .ThenBy(package => String.Join(',', package.Select(value => value.Id)))
                   .FirstOrDefault()
               ?? [];

        static double PackageDuration(IReadOnlyList<Clip> package) =>
            package.Sum(value => value.Duration) + Math.Max(0, package.Count - 1) * BoundarySilenceSamples / 24000d;

        static bool BetterSameDuration(IReadOnlyList<Clip> candidate, IReadOnlyList<Clip> existing)
        {
            var candidateObserved = candidate.Count(value => value.Origin != "curated");
            var existingObserved = existing.Count(value => value.Origin != "curated");
            if (candidateObserved != existingObserved) return candidateObserved < existingObserved;
            var candidatePreferred = candidate.Any(value => value.Priority >= 100);
            var existingPreferred = existing.Any(value => value.Priority >= 100);
            if (candidatePreferred != existingPreferred) return candidatePreferred;
            if (candidate.Count != existing.Count) return candidate.Count < existing.Count;
            return String.CompareOrdinal(String.Join(',', candidate.Select(value => value.Id)),
                String.Join(',', existing.Select(value => value.Id))) < 0;
        }
    }, token);

    private async Task ForgetPcmAsync(IReadOnlyList<Clip> clips, CancellationToken token)
    {
        await database.WriteAsync(async connection =>
        {
            foreach (var clip in clips)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE official_reference_clip SET pcm_path=NULL WHERE id=$id";
                command.Parameters.AddWithValue("$id", clip.Id);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
        foreach (var clip in clips)
            try { File.Delete(clip.Path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        await CleanupOrphanPcmAsync(token).ConfigureAwait(false);
    }

    private async Task CleanupOrphanPcmAsync(CancellationToken token)
    {
        var referenced = await database.ReadAsync(async connection =>
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pcm_path FROM official_reference_clip WHERE pcm_path IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Path.GetFullPath(reader.GetString(0)));
            return result;
        }, token).ConfigureAwait(false);
        foreach (var path in Directory.EnumerateFiles(directory, "*.f32*"))
        {
            token.ThrowIfCancellationRequested();
            if (referenced.Contains(Path.GetFullPath(path))) continue;
            try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new ArgumentException("Official reference language must be English, Japanese, German, or French", nameof(language));
        return language.Trim().ToLowerInvariant() switch
        {
            "en" or "eng" or "english" => "english",
            "ja" or "jpn" or "japanese" => "japanese",
            "de" or "deu" or "german" => "german",
            "fr" or "fra" or "french" => "french",
            _ => throw new ArgumentException("Official reference language must be English, Japanese, German, or French", nameof(language)),
        };
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record Clip(long Id, string SourceHash, string Language, string Transcript, string Path,
        double Duration, string? ScdPath, uint? SoundNumber, string Origin, int Priority);

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
        gate.Dispose();
        shutdown.Dispose();
    }
}
