using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Data.Sqlite;
using Resonance.Data;
using Resonance.Tts;

namespace Resonance.Game;

/// <summary>Outcome of attempting to store one observed official voice source.</summary>
public enum OfficialReferenceObservationStatus
{
    Duplicate,
    Pending,
    Stored,
}

/// <summary>Storage outcome and duration for an observed official voice source.</summary>
public sealed record OfficialReferenceObservationResult(
    OfficialReferenceObservationStatus Status,
    double DurationSeconds);

public sealed class OfficialReferenceBuilder : IAsyncDisposable
{
    public const double RequiredSeconds = 10.0;
    private static readonly TimeSpan UncertainOwnerGrace = TimeSpan.FromHours(1);
    private const double MaximumPackageSeconds = 12.0;
    private const int BoundarySilenceSamples = 3600;
    private readonly Database database;
    private readonly VoiceRegistry voices;
    private readonly ScdExtractor extractor;
    private readonly string directory;
    private readonly string cleanupLeasePath;
    private readonly string modelHash;
    private readonly Func<ReadOnlyMemory<float>, string, CancellationToken, ValueTask<VoiceReference>> extractReference;
    private readonly string instanceNonce = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly object disposeGate = new();
    private Task? disposeTask;
    private int disposed;

    public event Action<long, StoredVoiceProfile>? ProfileBuilt;

    public OfficialReferenceBuilder(Database database, VoiceRegistry voices, ITtsRuntime runtime,
        ScdExtractor extractor, string directory, string modelHash,
        Func<ReadOnlyMemory<float>, string, CancellationToken, ValueTask<VoiceReference>>? extractReference = null)
    {
        this.database = database;
        this.voices = voices;
        this.extractor = extractor;
        this.directory = directory;
        cleanupLeasePath = Path.Combine(directory, ".cleanup.lock");
        this.modelHash = modelHash;
        this.extractReference = extractReference ?? runtime.ExtractReferenceAsync;
        Directory.CreateDirectory(directory);
    }

    public async Task<OfficialReferenceObservationResult> ObserveAsync(long speakerId, string scdPath, uint soundNumber, string transcript,
        string language, CancellationToken token)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        token = linked.Token;
        language = NormalizeLanguage(language);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var sourceHash = Hash($"{scdPath.ToLowerInvariant()}\n{soundNumber}");
            if (await PrepareSourceAsync(speakerId, sourceHash, language, token).ConfigureAwait(false))
            {
                await RepairSourceMetadataAsync(speakerId, sourceHash, language, scdPath, soundNumber, transcript, token)
                    .ConfigureAwait(false);
                return new(OfficialReferenceObservationStatus.Duplicate, 0);
            }
            await PersistPendingObservationAsync(speakerId, sourceHash, transcript, language, scdPath, soundNumber, token)
                .ConfigureAwait(false);
            return new(OfficialReferenceObservationStatus.Pending, 0);
        }
        finally { gate.Release(); }
    }

    public async Task AddCuratedAsync(long speakerId, OfficialVoiceSource source, string language,
        int catalogVersion, CancellationToken token)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        token = linked.Token;
        var sourceHash = Hash($"{source.ScdPath.ToLowerInvariant()}\n{source.SoundNumber}");
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var pcm = await extractor.ExtractMono24KhzAsync(source.ScdPath, source.SoundNumber, token).ConfigureAwait(false);
            await AddPcmCoreAsync(speakerId, sourceHash, source.Transcript, language, pcm, token,
                source.ScdPath, source.SoundNumber, "curated", catalogVersion, restoreExisting: true,
                sourcePriority: source.Preferred ? 100 : 50, gateAlreadyHeld: true).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task ProcessPendingAsync(string language, CancellationToken token)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        token = linked.Token;
        language = NormalizeLanguage(language);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await CleanupOrphanPcmAsync(token).ConfigureAwait(false);
            await ConsumePendingForStableProfilesAsync(language, token).ConfigureAwait(false);
            await RetryPendingDecodesAsync(language, token, gateAlreadyHeld: true).ConfigureAwait(false);
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
            if (await HasPendingDecodesAsync(language, token).ConfigureAwait(false))
                throw new InvalidOperationException(
                    "Official voice source decoding remains pending; retry will resume at safe idle");
        }
        finally { gate.Release(); }
    }

    public async Task<StoredVoiceProfile?> RebuildPersistedAsync(
        long speakerId,
        string language,
        CancellationToken token)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        token = linked.Token;
        language = NormalizeLanguage(language);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
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
            var restored = false;
            foreach (var source in sources)
            {
                try
                {
                    var pcm = await extractor.ExtractMono24KhzAsync(source.Path, source.Sound, token).ConfigureAwait(false);
                    await AddPcmCoreAsync(speakerId, source.Hash, source.Transcript, language, pcm, token,
                        source.Path, source.Sound, source.Origin, source.Catalog, true, source.Priority,
                        buildProfile: false, gateAlreadyHeld: true).ConfigureAwait(false);
                    restored = true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    continue;
                }
            }
            if (!restored) return null;
            await TryBuildAsync(speakerId, language, token, force: true).ConfigureAwait(false);
            return await voices.GetBestVoiceAsync(speakerId, language, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal async Task<bool> AddPcmAsync(long speakerId, string sourceHash, string transcript, string language,
        float[] pcm, CancellationToken token, string? scdPath = null, uint? soundNumber = null,
        string sourceOrigin = "legacy", int? catalogVersion = null, bool restoreExisting = false,
        int sourcePriority = 0, bool buildProfile = true, bool forceBuild = false)
    {
        ThrowIfDisposed();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        return await AddPcmCoreAsync(speakerId, sourceHash, transcript, language, pcm, linked.Token, scdPath,
            soundNumber, sourceOrigin, catalogVersion, restoreExisting, sourcePriority, buildProfile, forceBuild).ConfigureAwait(false);
    }

    private async Task<bool> AddPcmCoreAsync(long speakerId, string sourceHash, string transcript, string language,
        float[] pcm, CancellationToken token, string? scdPath, uint? soundNumber,
        string sourceOrigin, int? catalogVersion, bool restoreExisting, int sourcePriority,
        bool buildProfile = true, bool forceBuild = false, bool gateAlreadyHeld = false)
    {
        language = NormalizeLanguage(language);
        if (pcm.Length < 24000 / 3 || pcm.Length > 24000 * MaximumPackageSeconds
            || pcm.Any(sample => !float.IsFinite(sample)))
            throw new InvalidDataException("Official reference clip has invalid duration or samples");
        var peak = pcm.Max(sample => Math.Abs(sample));
        if (peak < 0.002f) throw new InvalidDataException("Official reference clip is effectively silent");

        if (!gateAlreadyHeld)
            await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!gateAlreadyHeld) ThrowIfDisposed();
            var exists = await PrepareSourceAsync(speakerId, sourceHash, language, token).ConfigureAwait(false);
            if (exists && !restoreExisting) return false;
            var path = Path.Combine(directory, $"{sourceHash}.{language}.f32");
            string? ownerPath = null;
            var temporary = path + $".{instanceNonce}.part";
            var backup = path + $".{instanceNonce}.previous";
            var committed = false;
            var moved = false;
            var backupCreated = false;
            FileStream? writerLease = null;
            try
            {
                ownerPath = Path.Combine(directory, $".{instanceNonce}.owner.json");
                writerLease = TryAcquireCleanupLease();
                if (writerLease is null) return false;
                PublishOwner(ownerPath, instanceNonce);
                var bytes = new byte[checked(pcm.Length * sizeof(float))];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
                if (File.Exists(path))
                {
                    File.Copy(path, backup, true);
                    backupCreated = true;
                }
                File.Move(temporary, path, true);
                moved = true;
                await database.WriteAsync(async connection =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = exists ? """
                        UPDATE official_reference_clip
                        SET transcript=$text,pcm_path=$path,duration_seconds=$duration,
                            scd_path=$scd,sound_number=$sound,source_origin=$origin,
                            source_priority=$priority,catalog_version=$catalog,validated_utc=$validated,
                            decode_status='ready',decode_error=NULL,decode_attempted_utc=$validated
                        WHERE speaker_id=$speaker AND source_hash=$source AND language=$language
                        """ : """
                        INSERT OR IGNORE INTO official_reference_clip(
                          speaker_id,source_hash,language,transcript,pcm_path,duration_seconds,
                          scd_path,sound_number,source_origin,source_priority,catalog_version,validated_utc,
                          decode_status,decode_error,decode_attempted_utc,created_utc)
                        VALUES($speaker,$source,$language,$text,$path,$duration,
                          $scd,$sound,$origin,$priority,$catalog,$validated,
                          'ready',NULL,$validated,$utc)
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
                committed = true;
            }
            catch
            {
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                if (!committed)
                {
                    if (moved)
                    {
                        try { File.Delete(path); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                        if (backupCreated)
                        {
                            try { File.Move(backup, path, true); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                        }
                    }
                    else if (backupCreated)
                        try { File.Delete(backup); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                }
                throw;
            }
            finally
            {
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                if (committed || !backupCreated)
                    try { File.Delete(backup); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                if (ownerPath is not null)
                {
                    try { File.Delete(ownerPath); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                    try { File.Delete(ownerPath + ".pending"); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                }
                writerLease?.Dispose();
            }

            if (buildProfile) await TryBuildAsync(speakerId, language, token, forceBuild).ConfigureAwait(false);
            return true;
        }
        finally
        {
            if (!gateAlreadyHeld) gate.Release();
        }
    }

    private async Task TryBuildAsync(long speakerId, string language, CancellationToken token, bool force = false)
    {
        language = NormalizeLanguage(language);
        var existing = await voices.GetBestVoiceAsync(speakerId, language, token).ConfigureAwait(false);
        if (!force && existing is { Kind: VoiceProfileKind.Official })
        {
            // The profile is intentionally stable. Source rows remain for
            // explicit regeneration, but pending PCM must not accumulate or
            // be selected again on every idle pass.
            var pending = await LoadAllPendingPcmAsync(speakerId, language, token).ConfigureAwait(false);
            if (pending.Count > 0) await ForgetPcmAsync(pending, token).ConfigureAwait(false);
            return;
        }
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
        var reference = await extractReference(samples.ToArray(), referenceText, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
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

    private async Task RetryPendingDecodesAsync(string language, CancellationToken token, bool gateAlreadyHeld)
    {
        var sources = await LoadPendingSourcesAsync(language, token).ConfigureAwait(false);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await MarkPendingDecodeAttemptAsync(source.Id, token).ConfigureAwait(false);
                var pcm = await extractor.ExtractMono24KhzAsync(source.ScdPath, source.SoundNumber, token)
                    .ConfigureAwait(false);
                await AddPcmCoreAsync(source.SpeakerId, source.SourceHash, source.Transcript, source.Language,
                    pcm, token, source.ScdPath, source.SoundNumber, source.Origin, source.CatalogVersion,
                    restoreExisting: true, sourcePriority: source.Priority, buildProfile: false,
                    gateAlreadyHeld: gateAlreadyHeld).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                await MarkPendingDecodeFailureAsync(source.Id, error, token).ConfigureAwait(false);
            }
        }
    }

    private Task ConsumePendingForStableProfilesAsync(string language, CancellationToken token) => database.WriteAsync(
        async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE official_reference_clip
                SET decode_status='consumed',decode_error=NULL
                WHERE language=$language AND pcm_path IS NULL
                  AND decode_status IN ('pending','failed')
                  AND EXISTS(
                    SELECT 1
                    FROM speaker_voice sv
                    JOIN voice_profile p ON p.id=sv.profile_id
                    WHERE sv.speaker_id=official_reference_clip.speaker_id
                      AND p.language=$language AND p.kind=$official)
                """;
            command.Parameters.AddWithValue("$language", language);
            command.Parameters.AddWithValue("$official", (int)VoiceProfileKind.Official);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, token);

    private Task<List<PendingSource>> LoadPendingSourcesAsync(string language, CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,speaker_id,source_hash,language,transcript,scd_path,sound_number,
                   source_origin,source_priority,catalog_version
            FROM official_reference_clip
            WHERE language=$language AND pcm_path IS NULL
              AND decode_status IN ('pending','failed')
              AND scd_path IS NOT NULL AND sound_number IS NOT NULL
            ORDER BY source_priority DESC,created_utc,id
            """;
        command.Parameters.AddWithValue("$language", language);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var sources = new List<PendingSource>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            sources.Add(new PendingSource(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), checked((uint)reader.GetInt64(6)),
                reader.GetString(7), reader.GetInt32(8), reader.IsDBNull(9) ? null : reader.GetInt32(9)));
        }
        return sources;
    }, token);

    private Task<bool> HasPendingDecodesAsync(string language, CancellationToken token) => database.ReadAsync(
        async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                  SELECT 1 FROM official_reference_clip
                  WHERE language=$language AND pcm_path IS NULL
                    AND decode_status IN ('pending','failed')
                    AND scd_path IS NOT NULL AND sound_number IS NOT NULL)
                """;
            command.Parameters.AddWithValue("$language", language);
            return Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 0;
        }, token);

    private Task MarkPendingDecodeAttemptAsync(long id, CancellationToken token) => database.WriteAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE official_reference_clip
            SET decode_status='pending',decode_error=NULL,decode_attempted_utc=$attempted
            WHERE id=$id AND pcm_path IS NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$attempted", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }, token);

    private Task MarkPendingDecodeFailureAsync(long id, Exception error, CancellationToken token) => database.WriteAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE official_reference_clip
            SET decode_status='failed',decode_error=$error,decode_attempted_utc=$attempted
            WHERE id=$id AND pcm_path IS NULL
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$error", error.Message.Length > 2048 ? error.Message[..2048] : error.Message);
        command.Parameters.AddWithValue("$attempted", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }, token);

    private Task PersistPendingObservationAsync(long speakerId, string sourceHash, string transcript,
        string language, string scdPath, uint soundNumber, CancellationToken token) => database.WriteAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO official_reference_clip(
              speaker_id,source_hash,language,transcript,pcm_path,duration_seconds,
              scd_path,sound_number,source_origin,source_priority,catalog_version,validated_utc,
              decode_status,decode_error,decode_attempted_utc,created_utc)
            VALUES($speaker,$source,$language,$text,NULL,0,$scd,$sound,'observed',0,NULL,$validated,
              'pending',NULL,NULL,$utc)
            ON CONFLICT(speaker_id,source_hash,language) DO UPDATE SET
              transcript=CASE WHEN excluded.transcript<>'' THEN excluded.transcript
                              ELSE official_reference_clip.transcript END,
              scd_path=COALESCE(official_reference_clip.scd_path,excluded.scd_path),
              sound_number=COALESCE(official_reference_clip.sound_number,excluded.sound_number),
              validated_utc=excluded.validated_utc,
              decode_status=CASE WHEN official_reference_clip.pcm_path IS NULL
                                 THEN 'pending' ELSE official_reference_clip.decode_status END,
              decode_error=CASE WHEN official_reference_clip.pcm_path IS NULL
                                THEN NULL ELSE official_reference_clip.decode_error END
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$source", sourceHash);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$text", transcript.Trim());
        command.Parameters.AddWithValue("$scd", scdPath);
        command.Parameters.AddWithValue("$sound", soundNumber);
        command.Parameters.AddWithValue("$validated", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }, token);

    private Task RepairSourceMetadataAsync(long speakerId, string sourceHash, string language,
        string scdPath, uint soundNumber, string transcript, CancellationToken token) => database.WriteAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE official_reference_clip
            SET transcript=CASE
                    WHEN source_origin='observed' AND $text<>'' THEN $text
                    ELSE transcript END,
                scd_path=COALESCE(scd_path,$scd),
                sound_number=COALESCE(sound_number,$sound),
                validated_utc=$validated,
                decode_status=CASE
                    WHEN pcm_path IS NULL AND decode_status='pending' THEN 'pending'
                    ELSE decode_status END
            WHERE speaker_id=$speaker AND source_hash=$source AND language=$language
              AND (source_origin='observed' OR scd_path IS NULL OR sound_number IS NULL)
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$source", sourceHash);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$text", transcript.Trim());
        command.Parameters.AddWithValue("$scd", scdPath);
        command.Parameters.AddWithValue("$sound", soundNumber);
        command.Parameters.AddWithValue("$validated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
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

    private Task<List<Clip>> LoadAllPendingPcmAsync(long speakerId, string language, CancellationToken token) => database.ReadAsync(async connection =>
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
        var clips = new List<Clip>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            clips.Add(new Clip(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : checked((uint)reader.GetInt64(7)), reader.GetString(8), reader.GetInt32(9)));
        return clips;
    }, token);

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
                command.CommandText = """
                    UPDATE official_reference_clip
                    SET pcm_path=NULL,decode_status='consumed',decode_error=NULL
                    WHERE id=$id
                    """;
                command.Parameters.AddWithValue("$id", clip.Id);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }, token).ConfigureAwait(false);
        using (var cleanupLease = TryAcquireCleanupLease())
        {
            if (cleanupLease is not null && !HasLiveOwner())
            {
                foreach (var clip in clips)
                {
                    if (!IsSafeRegularFile(clip.Path)) continue;
                    try { File.Delete(clip.Path); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        await CleanupOrphanPcmAsync(token).ConfigureAwait(false);
    }

    private async Task CleanupOrphanPcmAsync(CancellationToken token)
    {
        using var cleanupLease = TryAcquireCleanupLease();
        if (cleanupLease is null) return;
        var referenced = await database.ReadAsync(async connection =>
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pcm_path FROM official_reference_clip WHERE pcm_path IS NOT NULL";
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(Path.GetFullPath(reader.GetString(0)));
            return result;
        }, token).ConfigureAwait(false);
        var liveOwner = false;
        var ownerPaths = Directory.EnumerateFiles(directory, "*.owner.json", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "*.owner.json.pending", SearchOption.TopDirectoryOnly))
            .Where(IsSafeRegularFile)
            .ToArray();
        foreach (var ownerPath in ownerPaths)
        {
            if (ownerPath.EndsWith(".owner.json.pending", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReapUncertainOwner(ownerPath)) liveOwner = true;
                continue;
            }
            try
            {
                if (!IsSafeRegularFile(ownerPath)) { liveOwner = true; continue; }
                var owner = JsonSerializer.Deserialize<ReferenceOwner>(await File.ReadAllTextAsync(ownerPath, token)
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
                // Unknown ownership is retained conservatively during a
                // development reload; source rows remain the authority.
                liveOwner = true;
            }
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.f32*"))
        {
            token.ThrowIfCancellationRequested();
            if (liveOwner) continue;
            if (!IsSafeRegularFile(path)) continue;
            if (referenced.Contains(Path.GetFullPath(path))) continue;
            if (!CanDeleteTransient(path)) continue;
            try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void PublishOwner(string ownerPath, string instanceNonce)
    {
        using var process = Process.GetCurrentProcess();
        var owner = new ReferenceOwner(process.Id, process.StartTime.ToUniversalTime().Ticks, instanceNonce);
        var temporary = ownerPath + ".pending";
        File.WriteAllText(temporary, JsonSerializer.Serialize(owner));
        File.Move(temporary, ownerPath, true);
    }

    private static bool IsOwnerAlive(ReferenceOwner owner)
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
                var owner = JsonSerializer.Deserialize<ReferenceOwner>(File.ReadAllText(ownerPath));
                if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                    || String.IsNullOrWhiteSpace(owner.InstanceNonce) || IsOwnerAlive(owner)) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or JsonException or InvalidOperationException or Win32Exception)
            {
                return true;
            }
        }
        return false;
    }

    private bool TryReapUncertainOwner(string ownerPath)
    {
        if (!IsSafeRegularFile(ownerPath)) return false;
        try
        {
            if (!IsSafeRegularFile(ownerPath)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(ownerPath) < UncertainOwnerGrace) return false;
            if (!IsSafeRegularFile(ownerPath)) return false;
            var owner = JsonSerializer.Deserialize<ReferenceOwner>(File.ReadAllText(ownerPath));
            if (owner is null || owner.ProcessId <= 0 || owner.ProcessStartUtcTicks <= 0
                || String.IsNullOrWhiteSpace(owner.InstanceNonce) || IsOwnerAlive(owner)) return false;
            File.Delete(ownerPath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                      or JsonException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private bool CanDeleteTransient(string path) => IsSafeRegularFile(path) && !HasLiveOwner();

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

    private static bool IsSafeRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Directory)
                && !attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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

    private sealed record ReferenceOwner(int ProcessId, long ProcessStartUtcTicks, string InstanceNonce);

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private sealed record PendingSource(long Id, long SpeakerId, string SourceHash, string Language,
        string Transcript, string ScdPath, uint SoundNumber, string Origin, int Priority, int? CatalogVersion);
    private sealed record Clip(long Id, string SourceHash, string Language, string Transcript, string Path,
        double Duration, string? ScdPath, uint? SoundNumber, string Origin, int Priority);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposed) != 0)
            throw new ObjectDisposedException(nameof(OfficialReferenceBuilder));
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            Interlocked.Exchange(ref disposed, 1);
            disposeTask = DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        await gate.WaitAsync().ConfigureAwait(false);
        gate.Release();
        gate.Dispose();
        shutdown.Dispose();
    }
}
