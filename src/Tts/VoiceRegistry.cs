using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Resonance.Data;

namespace Resonance.Tts;

public enum VoiceProfileKind { Designed = 1, Official = 2 }

public record SpeakerMetadata(
    int? Gender = null,
    int? Race = null,
    int? Tribe = null,
    int? Body = null,
    int? Height = null,
    int? MuscleMass = null,
    long? ModelCharaId = null,
    string? Sex = null,
    string? BodyType = null,
    string? Age = null,
    string? Physique = null,
    string? Register = null,
    string? Personality = null,
    string? Class = null,
    string? Culture = null,
    string? Species = null,
    string? Faction = null,
    string? VariantTraitsJson = null,
    string? EvidenceSource = null)
{
    public int? Muscle => MuscleMass;
    public long? ModelChara => ModelCharaId;

    public SpeakerMetadata Normalized() => this with
    {
        Sex = NormalizeToken(Sex),
        BodyType = NormalizeToken(BodyType),
        Age = NormalizeToken(Age),
        Physique = NormalizeToken(Physique),
        Register = NormalizeToken(Register),
        Personality = NormalizeToken(Personality),
        Class = NormalizeToken(Class),
        Culture = NormalizeToken(Culture),
        Species = NormalizeToken(Species),
        Faction = NormalizeToken(Faction),
        EvidenceSource = NormalizeToken(EvidenceSource),
    };

    private static string? NormalizeToken(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

// Compatibility name for the pre-v4 registry API. Persistence accepts the typed
// SpeakerMetadata base and does not inspect runtime evidence shapes.
public sealed record SpeakerEvidence(
    int? Gender = null,
    int? Race = null,
    int? Tribe = null,
    int? Body = null,
    int? Height = null,
    int? MuscleMass = null,
    long? ModelCharaId = null,
    string? Age = null,
    string? Physique = null,
    string? Register = null,
    string? Personality = null,
    string? Class = null,
    string? Culture = null,
    string? Species = null,
    string? Faction = null,
    string? VariantTraitsJson = null,
    string? EvidenceSource = null,
    string? Sex = null,
    string? BodyType = null)
    : SpeakerMetadata(Gender, Race, Tribe, Body, Height, MuscleMass, ModelCharaId, Sex, BodyType,
        Age, Physique, Register, Personality, Class, Culture, Species, Faction, VariantTraitsJson, EvidenceSource);

public sealed record SpeakerIdentity(
    long Id,
    string StableKey,
    uint? NpcBaseId,
    string DisplayName,
    uint TerritoryId,
    int? Gender = null,
    int? Race = null,
    int? Tribe = null,
    int? Body = null,
    int? Height = null,
    int? MuscleMass = null,
    long? ModelCharaId = null)
{
    public int? Muscle => MuscleMass;
    public long? ModelChara => ModelCharaId;
    public SpeakerMetadata? Metadata { get; init; }
    public string? Sex => Metadata?.Sex;
    public string? BodyType => Metadata?.BodyType;
}

public sealed record SpeakerCasting(
    long SpeakerId,
    string DomainId,
    string? VariantTraitsJson,
    string EvidenceSource,
    uint TerritoryId,
    int CatalogVersion,
    bool IsStable,
    string AssignedUtc);

public sealed record CastingSlot(
    string TemplateId,
    string? TraitsJson = null,
    long Sequence = 0);

public sealed record StoredVoiceProfile(
    string Id,
    VoiceProfileKind Kind,
    string Language,
    string ModelHash,
    int? PaletteVersion,
    string? DesignInstruction,
    long? Seed,
    VoiceReference Reference,
    string ProfileHash,
    string? SourceMetadata = null,
    string? DomainId = null,
    int? CatalogVersion = null,
    string? TraitsJson = null)
{
    public string? VariantTraitsJson => TraitsJson;
}

public sealed record NamedVoiceProfile(string DisplayName, StoredVoiceProfile Profile);

public sealed class VoiceRegistry(Database database)
{
    public Task<SpeakerIdentity> ResolveSpeakerAsync(
        string stableKey,
        uint? npcBaseId,
        string displayName,
        uint territoryId,
        string language,
        CancellationToken token) => ResolveSpeakerAsync(
            stableKey, npcBaseId, displayName, territoryId, language, null, token);

    public Task<SpeakerIdentity> ResolveSpeakerAsync(
        string stableKey,
        uint? npcBaseId,
        string displayName,
        uint territoryId,
        string language,
        SpeakerMetadata? evidence,
        CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var transaction = connection.BeginTransaction();
        var speaker = await UpsertSpeaker(connection, transaction, stableKey, npcBaseId, displayName,
            territoryId, evidence, token).ConfigureAwait(false);
        await using var alias = connection.CreateCommand();
        alias.Transaction = transaction;
        alias.CommandText = "INSERT OR IGNORE INTO speaker_alias(speaker_id, language, alias) VALUES($id,$language,$alias)";
        alias.Parameters.AddWithValue("$id", speaker.Id);
        alias.Parameters.AddWithValue("$language", language);
        alias.Parameters.AddWithValue("$alias", displayName);
        await alias.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return speaker;
    }, token);

    public Task<SpeakerIdentity> ResolveSpeakerAsync(
        string stableKey,
        uint? npcBaseId,
        string displayName,
        uint territoryId,
        string language,
        CancellationToken token,
        SpeakerMetadata? evidence) => ResolveSpeakerAsync(
            stableKey, npcBaseId, displayName, territoryId, language, evidence, token);

    public Task<SpeakerIdentity> UpsertSpeakerAsync(
        string stableKey,
        uint? npcBaseId,
        string displayName,
        uint territoryId,
        string language,
        SpeakerMetadata? evidence,
        CancellationToken token) => ResolveSpeakerAsync(
            stableKey, npcBaseId, displayName, territoryId, language, evidence, token);

    public Task<StoredVoiceProfile?> GetBestVoiceAsync(long speakerId, string language, CancellationToken token) =>
        database.ReadAsync(connection => GetBestVoiceCore(connection, speakerId, language, token), token);

    public Task<StoredVoiceProfile?> GetBestVoiceByStableKeyAsync(
        string stableKey,
        string language,
        CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM speaker WHERE stable_key=$key";
        command.Parameters.AddWithValue("$key", stableKey);
        var speakerId = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return speakerId is long id
            ? await GetBestVoiceCore(connection, id, language, token).ConfigureAwait(false)
            : null;
    }, token);

    public Task<IReadOnlyList<NamedVoiceProfile>> GetOfficialVoiceProfilesAsync(
        string language,
        CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.display_name,
                   p.id,p.kind,p.language,p.model_hash,p.palette_version,p.design_instruction,p.seed,
                   p.ref_text,p.speaker_embedding,p.rvq_codes,p.rvq_length,p.codebooks,
                   p.domain_id,p.catalog_version,p.traits_json,p.variant_traits_json,
                   p.source_metadata,p.profile_hash,p.created_utc
            FROM speaker_voice sv
            JOIN speaker s ON s.id=sv.speaker_id
            JOIN voice_profile p ON p.id=sv.profile_id
            WHERE p.kind=$kind AND p.language=$language
            ORDER BY s.display_name COLLATE NOCASE,p.created_utc DESC,p.id DESC
            """;
        command.Parameters.AddWithValue("$kind", (int)VoiceProfileKind.Official);
        command.Parameters.AddWithValue("$language", language);
        var result = new List<NamedVoiceProfile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var displayName = reader.GetString(0);
            if (!seen.Add(displayName)) continue;
            result.Add(new(displayName, ReadProfile(reader, 1)));
        }
        return (IReadOnlyList<NamedVoiceProfile>)result;
    }, token);

    public async Task<StoredVoiceProfile?> GetBestVoiceAsync(long speakerId, CancellationToken token)
    {
        // Compatibility for callers that have not migrated to a language-aware lookup. Returning
        // nothing when several languages exist prevents an accidental cross-language reuse.
        return await database.ReadAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(DISTINCT p.language) FROM speaker_voice sv JOIN voice_profile p ON p.id=sv.profile_id WHERE sv.speaker_id=$speaker";
            command.Parameters.AddWithValue("$speaker", speakerId);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1) return null;
            command.CommandText = "SELECT p.language FROM speaker_voice sv JOIN voice_profile p ON p.id=sv.profile_id WHERE sv.speaker_id=$speaker LIMIT 1";
            var language = (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return language is null ? null : await GetBestVoiceCore(connection, speakerId, language, token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    public Task<StoredVoiceProfile> SaveAndAssignAsync(long speakerId, StoredVoiceProfile profile, CancellationToken token) =>
        database.ReadAsync(async connection =>
        {
            await using var transaction = connection.BeginTransaction();
            var profileId = await InsertProfile(connection, transaction, profile, token).ConfigureAwait(false);
            await using var assign = connection.CreateCommand();
            assign.Transaction = transaction;
            assign.CommandText = """
                INSERT INTO speaker_voice(speaker_id,profile_id,priority,assigned_utc)
                VALUES($speaker,$profile,$priority,$utc)
                ON CONFLICT(speaker_id,profile_id) DO UPDATE SET priority=excluded.priority
                """;
            assign.Parameters.AddWithValue("$speaker", speakerId);
            assign.Parameters.AddWithValue("$profile", profileId);
            assign.Parameters.AddWithValue("$priority", profile.Kind == VoiceProfileKind.Official ? 200 : 100);
            assign.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
            await assign.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return profileId == profile.Id ? profile : profile with { Id = profileId };
        }, token);

    public Task<SpeakerCasting?> GetSpeakerCastingAsync(long speakerId, CancellationToken token) =>
        database.ReadAsync(connection => GetSpeakerCastingCore(connection, speakerId, token), token);

    public Task<SpeakerCasting?> GetCastingAsync(long speakerId, CancellationToken token) =>
        GetSpeakerCastingAsync(speakerId, token);

    [Obsolete("Speaker casting is global and no longer language-specific.")]
    public Task<SpeakerCasting?> GetSpeakerCastingAsync(long speakerId, string _, CancellationToken token) =>
        GetSpeakerCastingAsync(speakerId, token);

    [Obsolete("Speaker casting is global and no longer language-specific.")]
    public Task<SpeakerCasting?> GetCastingAsync(long speakerId, string _, CancellationToken token) =>
        GetSpeakerCastingAsync(speakerId, token);

    public Task<SpeakerCasting> SaveSpeakerCastingAsync(
        long speakerId,
        string domainId,
        string? variantTraitsJson,
        string? evidenceSource,
        uint territoryId,
        int catalogVersion,
        bool isStable,
        CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO speaker_casting(
              speaker_id,domain_id,variant_traits_json,evidence_source,
              territory_id,catalog_version,is_stable,assigned_utc)
            VALUES($speaker,$domain,$traits,$source,$territory,$catalog,$stable,$utc)
            ON CONFLICT(speaker_id) DO UPDATE SET
              domain_id=excluded.domain_id,
              variant_traits_json=excluded.variant_traits_json,
              evidence_source=excluded.evidence_source,
              territory_id=excluded.territory_id,
              catalog_version=excluded.catalog_version,
              is_stable=excluded.is_stable,
              assigned_utc=excluded.assigned_utc
            WHERE speaker_casting.is_stable=0
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$domain", domainId);
        command.Parameters.AddWithValue("$traits", variantTraitsJson is null ? DBNull.Value : variantTraitsJson);
        command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(evidenceSource) ? "unknown" : evidenceSource);
        command.Parameters.AddWithValue("$territory", territoryId);
        command.Parameters.AddWithValue("$catalog", catalogVersion);
        command.Parameters.AddWithValue("$stable", isStable ? 1 : 0);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var casting = await GetSpeakerCastingCore(connection, speakerId, token, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return casting ?? throw new InvalidOperationException("Speaker casting upsert returned no row");
    }, token);

    public Task<SpeakerCasting> SaveCastingAsync(
        long speakerId,
        string domainId,
        string? variantTraitsJson,
        string? evidenceSource,
        uint territoryId,
        int catalogVersion,
        bool isStable,
        CancellationToken token) => SaveSpeakerCastingAsync(
            speakerId, domainId, variantTraitsJson, evidenceSource,
            territoryId, catalogVersion, isStable, token);

    public Task<SpeakerCasting> SaveSpeakerCastingAsync(
        SpeakerCasting casting,
        CancellationToken token) => SaveSpeakerCastingAsync(
            casting.SpeakerId, casting.DomainId, casting.VariantTraitsJson,
            casting.EvidenceSource, casting.TerritoryId, casting.CatalogVersion, casting.IsStable, token);

    [Obsolete("Speaker casting is global and no longer language-specific.")]
    public Task<SpeakerCasting> SaveSpeakerCastingAsync(
        long speakerId,
        string language,
        string domainId,
        string? variantTraitsJson,
        string? evidenceSource,
        uint territoryId,
        int catalogVersion,
        bool isStable,
        CancellationToken token) => SaveSpeakerCastingAsync(
            speakerId, domainId, variantTraitsJson, evidenceSource,
            territoryId, catalogVersion, isStable, token);

    [Obsolete("Speaker casting is global and no longer language-specific.")]
    public Task<SpeakerCasting> SaveSpeakerCastingAsync(
        long speakerId,
        string language,
        string domainId,
        string? variantTraitsJson,
        string? evidenceSource,
        uint territoryId,
        int catalogVersion,
        CancellationToken token) => SaveSpeakerCastingAsync(
            speakerId, domainId, variantTraitsJson, evidenceSource,
            territoryId, catalogVersion, true, token);

    [Obsolete("Speaker casting is global and no longer language-specific.")]
    public Task<SpeakerCasting> SaveSpeakerCastingAsync(
        long speakerId,
        string language,
        string domainId,
        string? variantTraitsJson,
        string? evidenceSource,
        uint territoryId,
        int catalogVersion,
        CancellationToken token,
        bool isStable) => SaveSpeakerCastingAsync(
            speakerId, domainId, variantTraitsJson, evidenceSource,
            territoryId, catalogVersion, isStable, token);

    public Task<StoredVoiceProfile?> TryAssignDomainPoolVoiceAsync(
        long speakerId,
        string domainId,
        string language,
        string sex,
        CancellationToken token) => TryAssignDomainPoolVoiceAsync(
            speakerId, domainId, language, sex, (string?)null, token);

    public Task<StoredVoiceProfile?> TryAssignDomainPoolVoiceAsync(
        long speakerId,
        string domainId,
        string language,
        string sex,
        string? knownTraitsJson,
        CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var transaction = connection.BeginTransaction();
        var existing = await GetExistingProfileId(connection, transaction, speakerId, language, token).ConfigureAwait(false);
        if (existing is not null)
        {
            var assigned = await GetProfileById(connection, existing, token, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return assigned;
        }

        var known = await LoadKnownTraits(connection, transaction, speakerId, knownTraitsJson, token).ConfigureAwait(false);
        var candidates = new List<PoolCandidate>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT pv.profile_id,COALESCE(pv.slot_traits_json,p.traits_json,p.variant_traits_json),pv.sequence
                FROM pool_voice pv
                JOIN voice_profile p ON p.id=pv.profile_id
                WHERE pv.domain_id=$domain AND pv.language=$language AND pv.sex=$sex
                  AND pv.state=0 AND pv.assigned_speaker_id IS NULL
                  AND p.language=$language AND p.kind=1
                  AND NOT EXISTS(SELECT 1 FROM speaker_voice sv WHERE sv.profile_id=pv.profile_id)
                ORDER BY pv.sequence,pv.profile_id
                LIMIT 5
                """;
            select.Parameters.AddWithValue("$domain", domainId);
            select.Parameters.AddWithValue("$language", language);
            select.Parameters.AddWithValue("$sex", NormalizeSex(sex));
            await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var slotTraits = reader.IsDBNull(1) ? null : reader.GetString(1);
                candidates.Add(new PoolCandidate(reader.GetString(0), slotTraits, reader.GetInt64(2), TraitScore(known, slotTraits)));
            }
        }

        var candidate = candidates
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Sequence)
            .ThenBy(value => value.ProfileId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null)
        {
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return null;
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE pool_voice
            SET state=1,assigned_speaker_id=$speaker
            WHERE profile_id=$profile AND state=0 AND assigned_speaker_id IS NULL
              AND NOT EXISTS(SELECT 1 FROM speaker_voice WHERE profile_id=$profile)
            """;
        update.Parameters.AddWithValue("$speaker", speakerId);
        update.Parameters.AddWithValue("$profile", candidate.ProfileId);
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return null;
        }

        await using var assign = connection.CreateCommand();
        assign.Transaction = transaction;
        assign.CommandText = """
            INSERT INTO speaker_voice(speaker_id,profile_id,priority,assigned_utc)
            VALUES($speaker,$profile,100,$utc)
            """;
        assign.Parameters.AddWithValue("$speaker", speakerId);
        assign.Parameters.AddWithValue("$profile", candidate.ProfileId);
        assign.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        await assign.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var result = await GetProfileById(connection, candidate.ProfileId, token, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return result;
    }, token);

    public Task<StoredVoiceProfile?> TryAssignPoolVoiceAsync(
        long speakerId,
        string domainId,
        string language,
        string sex,
        string? knownTraitsJson,
        CancellationToken token) => TryAssignDomainPoolVoiceAsync(
            speakerId, domainId, language, sex, knownTraitsJson, token);

    public Task<StoredVoiceProfile?> TryAssignPoolVoiceAsync(
        long speakerId,
        string domainId,
        string language,
        string sex,
        CancellationToken token) => TryAssignDomainPoolVoiceAsync(
            speakerId, domainId, language, sex, token);

    public Task<int> CountReadyDomainPoolAsync(string domainId, string language, string sex, CancellationToken token) =>
        CountPoolCoreAsync(domainId, language, sex, true, token);

    public Task<int> CountReadyByDomainAsync(string domainId, string language, string sex, CancellationToken token) =>
        CountReadyDomainPoolAsync(domainId, language, sex, token);

    public Task<int> CountDomainPoolAsync(string domainId, string language, string sex, CancellationToken token) =>
        CountPoolCoreAsync(domainId, language, sex, false, token);

    public Task<int> CountByDomainAsync(string domainId, string language, string sex, CancellationToken token) =>
        CountDomainPoolAsync(domainId, language, sex, token);

    public Task<int> CountReadyPoolAsync(string domainId, string language, string sex, CancellationToken token) =>
        CountReadyDomainPoolAsync(domainId, language, sex, token);

    public Task<int> CountPoolAsync(string domainId, string language, string sex, CancellationToken token) =>
        CountDomainPoolAsync(domainId, language, sex, token);

    public Task<long> ReserveDomainPoolSequenceAsync(
        string domainId,
        string language,
        string sex,
        CancellationToken token) => database.ReadAsync(async connection =>
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pool_sequence(domain_id,language,sex,next_sequence)
            VALUES($domain,$language,$sex,1)
            ON CONFLICT(domain_id,language,sex)
            DO UPDATE SET next_sequence=pool_sequence.next_sequence+1
            RETURNING next_sequence-1
            """;
        command.Parameters.AddWithValue("$domain", domainId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$sex", NormalizeSex(sex));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
    }, token);

    public Task<long> ReservePoolSequenceAsync(
        string domainId,
        string language,
        string sex,
        CancellationToken token) => ReserveDomainPoolSequenceAsync(domainId, language, sex, token);

    public Task<long> ReserveSequenceAsync(
        string domainId,
        string language,
        string sex,
        CancellationToken token) => ReserveDomainPoolSequenceAsync(domainId, language, sex, token);

    public Task ClearReadyDomainPoolAsync(string domainId, string language, CancellationToken token) =>
        ClearReadyDomainsCoreAsync([domainId], language, token);

    public Task ClearReadyPoolAsync(string domainId, string language, CancellationToken token) =>
        ClearReadyDomainPoolAsync(domainId, language, token);

    public Task ClearReadySelectedDomainsAsync(IEnumerable<string> domainIds, string language, CancellationToken token) =>
        ClearReadyDomainsCoreAsync(domainIds, language, token);

    public Task ClearReadyDomainsAsync(IEnumerable<string> domainIds, string language, CancellationToken token) =>
        ClearReadyDomainsCoreAsync(domainIds, language, token);

    public Task ClearReadyPoolsAsync(IEnumerable<string> domainIds, string language, CancellationToken token) =>
        ClearReadySelectedDomainsAsync(domainIds, language, token);

    public Task ClearReadyPoolAsync(IEnumerable<string> domainIds, string language, CancellationToken token) =>
        ClearReadySelectedDomainsAsync(domainIds, language, token);

    public Task<StoredVoiceProfile> SaveDomainPoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        string templateId,
        string? slotTraitsJson,
        StoredVoiceProfile profile,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
            domainId, language, sex, templateId, slotTraitsJson, profile.Seed ?? 0, profile, token);

    public Task<StoredVoiceProfile> SaveDomainPoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        string templateId,
        string? slotTraitsJson,
        long sequence,
        StoredVoiceProfile profile,
        CancellationToken token) => database.ReadAsync(async connection =>
    {
        if (profile.Kind != VoiceProfileKind.Designed)
            throw new InvalidOperationException("Only designed profiles can enter a casting-domain pool");
        if (!string.Equals(profile.Language, language, StringComparison.Ordinal))
            throw new ArgumentException("Pool language must match the stored voice profile language", nameof(profile));
        if (profile.DomainId is not null && !string.Equals(profile.DomainId, domainId, StringComparison.Ordinal))
            throw new ArgumentException("Pool domain must match the stored voice profile domain", nameof(profile));
        profile = profile with { DomainId = profile.DomainId ?? domainId };
        await using var transaction = connection.BeginTransaction();
        var profileId = await InsertProfile(connection, transaction, profile, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO pool_voice(
              domain_id,language,sex,profile_id,state,slot_traits_json,template_id,sequence)
            VALUES($domain,$language,$sex,$profile,0,$traits,$template,$sequence)
            """;
        command.Parameters.AddWithValue("$domain", domainId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$sex", NormalizeSex(sex));
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$traits", slotTraitsJson is null ? DBNull.Value : slotTraitsJson);
        command.Parameters.AddWithValue("$template", string.IsNullOrWhiteSpace(templateId) ? "default" : templateId);
        command.Parameters.AddWithValue("$sequence", sequence);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return profileId == profile.Id ? profile : profile with { Id = profileId };
    }, token);

    public Task<StoredVoiceProfile> SaveDomainPoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        StoredVoiceProfile profile,
        string? slotTraitsJson,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
        domainId, language, sex, "default", slotTraitsJson, profile, token);

    public Task<StoredVoiceProfile> SaveDomainPoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        CastingSlot slot,
        StoredVoiceProfile profile,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
            domainId, language, sex, slot.TemplateId, slot.TraitsJson, slot.Sequence, profile, token);

    public Task<StoredVoiceProfile> SaveDomainPoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        StoredVoiceProfile profile,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
            domainId, language, sex, "default", null, profile, token);

    public Task<StoredVoiceProfile> SavePoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        string templateId,
        string? slotTraitsJson,
        StoredVoiceProfile profile,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
        domainId, language, sex, templateId, slotTraitsJson, profile, token);

    public Task<StoredVoiceProfile> SavePoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        CastingSlot slot,
        StoredVoiceProfile profile,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
            domainId, language, sex, slot, profile, token);

    public Task<StoredVoiceProfile> SavePoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        StoredVoiceProfile profile,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
            domainId, language, sex, profile, token);

    public Task<StoredVoiceProfile> SavePoolVoiceAsync(
        string domainId,
        string language,
        string sex,
        StoredVoiceProfile profile,
        string? slotTraitsJson,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
            domainId, language, sex, profile, slotTraitsJson, token);

    [Obsolete("Migrate callers to the domain-keyed pool API.")]
    public Task<StoredVoiceProfile?> TryAssignPoolVoiceAsync(
        long speakerId,
        uint territoryId,
        string language,
        string archetype,
        CancellationToken token) => TryAssignDomainPoolVoiceAsync(
            speakerId, LegacyDomain(territoryId), language, NormalizeSex(archetype), token);

    [Obsolete("Migrate callers to the domain-keyed pool API.")]
    public Task<int> CountReadyPoolAsync(uint territoryId, string language, string archetype, CancellationToken token) =>
        CountReadyDomainPoolAsync(LegacyDomain(territoryId), language, NormalizeSex(archetype), token);

    [Obsolete("Migrate callers to the domain-keyed pool API.")]
    public Task<int> CountPoolAsync(uint territoryId, string language, string archetype, CancellationToken token) =>
        CountDomainPoolAsync(LegacyDomain(territoryId), language, NormalizeSex(archetype), token);

    [Obsolete("Migrate callers to the domain-keyed pool API.")]
    public Task<long> ReservePoolSequenceAsync(uint territoryId, string language, string archetype, CancellationToken token) =>
        ReserveDomainPoolSequenceAsync(LegacyDomain(territoryId), language, NormalizeSex(archetype), token);

    [Obsolete("Migrate callers to the domain-keyed pool API.")]
    public Task ClearReadyPoolAsync(uint territoryId, string language, CancellationToken token) =>
        ClearReadyDomainPoolAsync(LegacyDomain(territoryId), language, token);

    [Obsolete("Migrate callers to the domain-keyed pool API.")]
    public Task<StoredVoiceProfile> SavePoolVoiceAsync(
        uint territoryId,
        string language,
        string archetype,
        StoredVoiceProfile profile,
        CancellationToken token) => SaveDomainPoolVoiceAsync(
            LegacyDomain(territoryId), language, NormalizeSex(archetype), archetype, null, profile, token);

    public static StoredVoiceProfile CreateProfile(
        VoiceProfileKind kind,
        string language,
        string modelHash,
        int? paletteVersion,
        string? instruction,
        long? seed,
        VoiceReference reference,
        string? sourceMetadata = null,
        string? domainId = null,
        int? catalogVersion = null,
        string? traitsJson = null)
    {
        var bytes = Encoding.UTF8.GetBytes(
            $"{kind}\0{language}\0{modelHash}\0{paletteVersion}\0{instruction}\0{seed}\0{domainId}\0{catalogVersion}\0{traitsJson}\0{reference.Transcript}");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes);
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(reference.SpeakerEmbedding.AsSpan()));
        hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(reference.RvqCodes.AsSpan()));
        var profileHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new(Guid.NewGuid().ToString("N"), kind, language, modelHash, paletteVersion, instruction, seed,
            reference, profileHash, sourceMetadata, domainId, catalogVersion, traitsJson);
    }

    public static StoredVoiceProfile CreateProfile(
        VoiceProfileKind kind,
        string language,
        string modelHash,
        int? paletteVersion,
        string? instruction,
        long? seed,
        VoiceReference reference,
        string? sourceMetadata,
        string? domainId,
        string catalogVersion,
        string? traitsJson = null)
    {
        int? parsedVersion = int.TryParse(catalogVersion, out var value) ? value : null;
        return CreateProfile(kind, language, modelHash, paletteVersion, instruction, seed, reference,
            sourceMetadata, domainId, parsedVersion, traitsJson);
    }

    private static async Task<SpeakerIdentity> UpsertSpeaker(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        uint? npcBaseId,
        string displayName,
        uint territoryId,
        SpeakerMetadata? evidence,
        CancellationToken token)
    {
        var normalizedEvidence = evidence?.Normalized();
        var traitsJson = await MergeSpeakerTraits(connection, transaction, key,
            SerializeMetadata(normalizedEvidence), token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO speaker(
              stable_key,npc_base_id,display_name,gender,race,tribe,body,
              height,muscle_mass,model_chara_id,speaker_traits_json,first_territory,created_utc)
            VALUES($key,$npc,$name,$gender,$race,$tribe,$body,$height,$muscle,$model,$traits,$territory,$utc)
            ON CONFLICT(stable_key) DO UPDATE SET
              display_name=excluded.display_name,
              npc_base_id=COALESCE(speaker.npc_base_id,excluded.npc_base_id),
              gender=COALESCE(excluded.gender,speaker.gender),
              race=COALESCE(excluded.race,speaker.race),
              tribe=COALESCE(excluded.tribe,speaker.tribe),
              body=COALESCE(excluded.body,speaker.body),
              height=COALESCE(excluded.height,speaker.height),
              muscle_mass=COALESCE(excluded.muscle_mass,speaker.muscle_mass),
              model_chara_id=COALESCE(excluded.model_chara_id,speaker.model_chara_id),
              speaker_traits_json=COALESCE(excluded.speaker_traits_json,speaker.speaker_traits_json)
            RETURNING id
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$npc", npcBaseId is null ? DBNull.Value : npcBaseId.Value);
        command.Parameters.AddWithValue("$name", displayName);
        command.Parameters.AddWithValue("$gender", normalizedEvidence?.Gender is null ? DBNull.Value : normalizedEvidence.Gender.Value);
        command.Parameters.AddWithValue("$race", normalizedEvidence?.Race is null ? DBNull.Value : normalizedEvidence.Race.Value);
        command.Parameters.AddWithValue("$tribe", normalizedEvidence?.Tribe is null ? DBNull.Value : normalizedEvidence.Tribe.Value);
        command.Parameters.AddWithValue("$body", normalizedEvidence?.Body is null ? DBNull.Value : normalizedEvidence.Body.Value);
        command.Parameters.AddWithValue("$height", normalizedEvidence?.Height is null ? DBNull.Value : normalizedEvidence.Height.Value);
        command.Parameters.AddWithValue("$muscle", normalizedEvidence?.MuscleMass is null ? DBNull.Value : normalizedEvidence.MuscleMass.Value);
        command.Parameters.AddWithValue("$model", normalizedEvidence?.ModelCharaId is null ? DBNull.Value : normalizedEvidence.ModelCharaId.Value);
        command.Parameters.AddWithValue("$traits", traitsJson is null ? DBNull.Value : traitsJson);
        command.Parameters.AddWithValue("$territory", territoryId);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT id,stable_key,npc_base_id,display_name,first_territory,
                   gender,race,tribe,body,height,muscle_mass,model_chara_id,speaker_traits_json
            FROM speaker WHERE id=$id
            """;
        select.Parameters.AddWithValue("$id", id);
        await using var reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidOperationException("Speaker upsert returned no row");
        var metadata = DeserializeMetadata(reader.IsDBNull(12) ? null : reader.GetString(12));
        metadata = metadata is null ? null : metadata with
        {
            Gender = reader.IsDBNull(5) ? metadata.Gender : reader.GetInt32(5),
            Race = reader.IsDBNull(6) ? metadata.Race : reader.GetInt32(6),
            Tribe = reader.IsDBNull(7) ? metadata.Tribe : reader.GetInt32(7),
            Body = reader.IsDBNull(8) ? metadata.Body : reader.GetInt32(8),
            Height = reader.IsDBNull(9) ? metadata.Height : reader.GetInt32(9),
            MuscleMass = reader.IsDBNull(10) ? metadata.MuscleMass : reader.GetInt32(10),
            ModelCharaId = reader.IsDBNull(11) ? metadata.ModelCharaId : reader.GetInt64(11),
        };
        return new SpeakerIdentity(
            reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : Convert.ToUInt32(reader.GetInt64(2)),
            reader.GetString(3), Convert.ToUInt32(reader.GetInt64(4)),
            reader.IsDBNull(5) ? null : reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.IsDBNull(9) ? null : reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11)) with { Metadata = metadata };
    }

    private static async Task<string?> MergeSpeakerTraits(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string stableKey,
        string? incomingJson,
        CancellationToken token)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT speaker_traits_json FROM speaker WHERE stable_key=$key";
        select.Parameters.AddWithValue("$key", stableKey);
        var existingValue = await select.ExecuteScalarAsync(token).ConfigureAwait(false);
        var existingJson = existingValue switch
        {
            null => null,
            DBNull => null,
            string value => value,
            _ => throw new InvalidOperationException(
                $"Speaker traits for '{stableKey}' returned {existingValue.GetType().Name}, expected TEXT")
        };
        if (incomingJson is null) return existingJson;

        var merged = DeserializeTraits(existingJson);
        OverlayTraits(merged, DeserializeTraits(incomingJson));
        return merged.Count == 0 ? null : JsonSerializer.Serialize(merged);
    }

    private static string? SerializeMetadata(SpeakerMetadata? metadata)
    {
        if (metadata is null) return null;
        var normalized = metadata.Normalized();
        var traits = DeserializeTraits(normalized.VariantTraitsJson);
        AddTrait(traits, "gender", normalized.Gender);
        AddTrait(traits, "race", normalized.Race);
        AddTrait(traits, "tribe", normalized.Tribe);
        AddTrait(traits, "body", normalized.Body);
        AddTrait(traits, "height", normalized.Height);
        AddTrait(traits, "muscle_mass", normalized.MuscleMass);
        AddTrait(traits, "model_chara_id", normalized.ModelCharaId);
        AddTrait(traits, "sex", normalized.Sex);
        AddTrait(traits, "body_type", normalized.BodyType);
        AddTrait(traits, "age", normalized.Age);
        AddTrait(traits, "physique", normalized.Physique);
        AddTrait(traits, "register", normalized.Register);
        AddTrait(traits, "personality", normalized.Personality);
        AddTrait(traits, "class", normalized.Class);
        AddTrait(traits, "culture", normalized.Culture);
        AddTrait(traits, "species", normalized.Species);
        AddTrait(traits, "faction", normalized.Faction);
        AddTrait(traits, "evidence_source", normalized.EvidenceSource);
        return traits.Count == 0 ? null : JsonSerializer.Serialize(traits);
    }

    private static SpeakerMetadata? DeserializeMetadata(string? json)
    {
        var traits = DeserializeTraits(json);
        if (traits.Count == 0) return null;
        return new SpeakerMetadata(
            ParseIntTrait(traits, "gender"), ParseIntTrait(traits, "race"), ParseIntTrait(traits, "tribe"),
            ParseIntTrait(traits, "body"), ParseIntTrait(traits, "height"), ParseIntTrait(traits, "muscle_mass"),
            ParseLongTrait(traits, "model_chara_id"), Trait(traits, "sex"), Trait(traits, "body_type"),
            Trait(traits, "age"), Trait(traits, "physique"), Trait(traits, "register"),
            Trait(traits, "personality"), Trait(traits, "class"), Trait(traits, "culture"),
            Trait(traits, "species"), Trait(traits, "faction"), json, Trait(traits, "evidence_source"));
    }

    private static void AddTrait(Dictionary<string, string> traits, string name, int? value) =>
        AddTrait(traits, name, value?.ToString(CultureInfo.InvariantCulture));

    private static void AddTrait(Dictionary<string, string> traits, string name, long? value) =>
        AddTrait(traits, name, value?.ToString(CultureInfo.InvariantCulture));

    private static void AddTrait(Dictionary<string, string> traits, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) traits[name] = value.Trim().ToLowerInvariant();
    }

    private static string? Trait(IReadOnlyDictionary<string, string> traits, string name) =>
        traits.TryGetValue(name, out var value) ? value : null;

    private static int? ParseIntTrait(IReadOnlyDictionary<string, string> traits, string name) =>
        traits.TryGetValue(name, out var value) && int.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? ParseLongTrait(IReadOnlyDictionary<string, string> traits, string name) =>
        traits.TryGetValue(name, out var value) && long.TryParse(value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static async Task<StoredVoiceProfile?> GetBestVoiceCore(
        SqliteConnection connection,
        long speakerId,
        string language,
        CancellationToken token,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = """
            SELECT p.id,p.kind,p.language,p.model_hash,p.palette_version,p.design_instruction,p.seed,
                   p.ref_text,p.speaker_embedding,p.rvq_codes,p.rvq_length,p.codebooks,
                   p.domain_id,p.catalog_version,p.traits_json,p.variant_traits_json,
                   p.source_metadata,p.profile_hash,p.created_utc
            FROM speaker_voice sv JOIN voice_profile p ON p.id=sv.profile_id
            WHERE sv.speaker_id=$speaker AND p.language=$language
            ORDER BY sv.priority DESC,p.created_utc DESC,p.id LIMIT 1
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$language", language);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadProfile(reader) : null;
    }

    private static async Task<SpeakerCasting?> GetSpeakerCastingCore(
        SqliteConnection connection,
        long speakerId,
        CancellationToken token,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = """
            SELECT speaker_id,domain_id,variant_traits_json,evidence_source,
                   territory_id,catalog_version,is_stable,assigned_utc
            FROM speaker_casting WHERE speaker_id=$speaker
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return new(
            reader.GetInt64(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
            Convert.ToUInt32(reader.GetInt64(4)), reader.GetInt32(5), reader.GetInt32(6) != 0,
            reader.GetString(7));
    }

    private static async Task<string?> GetExistingProfileId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long speakerId,
        string language,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.id FROM speaker_voice sv JOIN voice_profile p ON p.id=sv.profile_id
            WHERE sv.speaker_id=$speaker AND p.language=$language
            ORDER BY sv.priority DESC,sv.assigned_utc DESC,p.id LIMIT 1
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        command.Parameters.AddWithValue("$language", language);
        return (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private static async Task<string> InsertProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredVoiceProfile profile,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO voice_profile(
              id,kind,language,model_hash,palette_version,design_instruction,seed,ref_text,
              speaker_embedding,rvq_codes,rvq_length,codebooks,domain_id,catalog_version,
              traits_json,variant_traits_json,source_metadata,profile_hash,created_utc)
            VALUES($id,$kind,$language,$model,$palette,$instruction,$seed,$text,$embedding,$codes,
                   $length,$codebooks,$domain,$catalog,$traits,$variant,$source,$hash,$utc)
            ON CONFLICT(profile_hash) DO UPDATE SET profile_hash=excluded.profile_hash
            RETURNING id
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$kind", (int)profile.Kind);
        command.Parameters.AddWithValue("$language", profile.Language);
        command.Parameters.AddWithValue("$model", profile.ModelHash);
        command.Parameters.AddWithValue("$palette", profile.PaletteVersion is null ? DBNull.Value : profile.PaletteVersion.Value);
        command.Parameters.AddWithValue("$instruction", profile.DesignInstruction is null ? DBNull.Value : profile.DesignInstruction);
        command.Parameters.AddWithValue("$seed", profile.Seed is null ? DBNull.Value : profile.Seed.Value);
        command.Parameters.AddWithValue("$text", profile.Reference.Transcript);
        command.Parameters.AddWithValue("$embedding", ToBytes(profile.Reference.SpeakerEmbedding));
        command.Parameters.AddWithValue("$codes", ToBytes(profile.Reference.RvqCodes));
        command.Parameters.AddWithValue("$length", profile.Reference.RvqLength);
        command.Parameters.AddWithValue("$codebooks", profile.Reference.Codebooks);
        command.Parameters.AddWithValue("$domain", profile.DomainId is null ? DBNull.Value : profile.DomainId);
        command.Parameters.AddWithValue("$catalog", profile.CatalogVersion is null ? DBNull.Value : profile.CatalogVersion.Value);
        command.Parameters.AddWithValue("$traits", profile.TraitsJson is null ? DBNull.Value : profile.TraitsJson);
        command.Parameters.AddWithValue("$variant", profile.TraitsJson is null ? DBNull.Value : profile.TraitsJson);
        command.Parameters.AddWithValue("$source", profile.SourceMetadata is null ? DBNull.Value : profile.SourceMetadata);
        command.Parameters.AddWithValue("$hash", profile.ProfileHash);
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O"));
        return (string)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Voice profile insert returned no ID"));
    }

    private static async Task<int> CountPoolCore(
        SqliteConnection connection,
        string domainId,
        string language,
        string sex,
        bool ready,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM pool_voice pv JOIN voice_profile p ON p.id=pv.profile_id
            WHERE pv.domain_id=$domain AND pv.language=$language AND pv.sex=$sex
              AND p.kind=1{(ready ? " AND pv.state=0 AND pv.assigned_speaker_id IS NULL AND NOT EXISTS(SELECT 1 FROM speaker_voice sv WHERE sv.profile_id=pv.profile_id)" : "")}
            """;
        command.Parameters.AddWithValue("$domain", domainId);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$sex", NormalizeSex(sex));
        return Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private Task<int> CountPoolCoreAsync(string domainId, string language, string sex, bool ready, CancellationToken token) =>
        database.ReadAsync(connection => CountPoolCore(connection, domainId, language, sex, ready, token), token);

    private async Task ClearReadyDomainsCoreAsync(IEnumerable<string> domainIds, string language, CancellationToken token)
    {
        var domains = domainIds.Distinct(StringComparer.Ordinal).ToArray();
        if (domains.Length == 0) return;
        await database.WriteAsync(async connection =>
        {
            await using var transaction = connection.BeginTransaction();
            await using var createKeys = connection.CreateCommand();
            createKeys.Transaction = transaction;
            createKeys.CommandText = "DROP TABLE IF EXISTS ready_profiles_to_delete; CREATE TEMP TABLE ready_profiles_to_delete(profile_id TEXT PRIMARY KEY);";
            await createKeys.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            foreach (var domain in domains)
            {
                await using var fillKeys = connection.CreateCommand();
                fillKeys.Transaction = transaction;
                fillKeys.CommandText = """
                    INSERT OR IGNORE INTO ready_profiles_to_delete(profile_id)
                    SELECT profile_id FROM pool_voice
                    WHERE domain_id=$domain AND language=$language AND state=0
                      AND assigned_speaker_id IS NULL
                      AND EXISTS(SELECT 1 FROM voice_profile WHERE id=pool_voice.profile_id AND kind=1)
                      AND NOT EXISTS(SELECT 1 FROM speaker_voice WHERE profile_id=pool_voice.profile_id)
                    """;
                fillKeys.Parameters.AddWithValue("$domain", domain);
                fillKeys.Parameters.AddWithValue("$language", language);
                await fillKeys.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                await using var deletePool = connection.CreateCommand();
                deletePool.Transaction = transaction;
                deletePool.CommandText = """
                    DELETE FROM pool_voice
                    WHERE domain_id=$domain AND language=$language AND state=0
                      AND assigned_speaker_id IS NULL
                      AND EXISTS(SELECT 1 FROM voice_profile WHERE id=pool_voice.profile_id AND kind=1)
                      AND NOT EXISTS(SELECT 1 FROM speaker_voice WHERE profile_id=pool_voice.profile_id)
                    """;
                deletePool.Parameters.AddWithValue("$domain", domain);
                deletePool.Parameters.AddWithValue("$language", language);
                await deletePool.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM voice_profile
                WHERE id IN (SELECT profile_id FROM ready_profiles_to_delete)
                  AND NOT EXISTS(SELECT 1 FROM line_cache WHERE profile_id=voice_profile.id)
                """;
            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await using var dropKeys = connection.CreateCommand();
            dropKeys.Transaction = transaction;
            dropKeys.CommandText = "DROP TABLE IF EXISTS ready_profiles_to_delete";
            await dropKeys.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, string>> LoadKnownTraits(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long speakerId,
        string? explicitTraitsJson,
        CancellationToken token)
    {
        var traits = DeserializeTraits(explicitTraitsJson);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.gender,s.race,s.tribe,s.body,s.height,s.muscle_mass,s.model_chara_id,
                   s.speaker_traits_json,c.variant_traits_json
            FROM speaker s LEFT JOIN speaker_casting c
              ON c.speaker_id=s.id
            WHERE s.id=$speaker
            """;
        command.Parameters.AddWithValue("$speaker", speakerId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            AddNumber(traits, "gender", reader, 0);
            AddNumber(traits, "race", reader, 1);
            AddNumber(traits, "tribe", reader, 2);
            AddNumber(traits, "body", reader, 3);
            AddNumber(traits, "height", reader, 4);
            AddNumber(traits, "muscle_mass", reader, 5);
            AddNumber(traits, "model_chara_id", reader, 6);
            if (!reader.IsDBNull(7)) MergeTraits(traits, DeserializeTraits(reader.GetString(7)));
            if (!reader.IsDBNull(8)) MergeTraits(traits, DeserializeTraits(reader.GetString(8)));
        }
        return traits;
    }

    private static void AddNumber(Dictionary<string, string> traits, string name, SqliteDataReader reader, int ordinal)
    {
        if (!reader.IsDBNull(ordinal)) traits.TryAdd(name, reader.GetValue(ordinal).ToString()!);
    }

    private static int TraitScore(IReadOnlyDictionary<string, string> known, string? slotTraitsJson)
    {
        var slot = DeserializeTraits(slotTraitsJson);
        var score = 0;
        foreach (var pair in slot)
        {
            if (known.TryGetValue(pair.Key, out var value) &&
                string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase)) score++;
        }
        return score;
    }

    private static Dictionary<string, string> DeserializeTraits(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return result;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var value = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(value)) result[property.Name] = value.Trim().ToLowerInvariant();
            }
        }
        catch (JsonException) { }
        return result;
    }

    private static void MergeTraits(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var pair in source) target.TryAdd(pair.Key, pair.Value);
    }

    private static void OverlayTraits(Dictionary<string, string> target, IReadOnlyDictionary<string, string> source)
    {
        foreach (var pair in source) target[pair.Key] = pair.Value;
    }

    private static StoredVoiceProfile ReadProfile(SqliteDataReader reader, int offset = 0)
    {
        var embeddingBytes = (byte[])reader[offset + 8];
        var codeBytes = (byte[])reader[offset + 9];
        var embedding = new float[embeddingBytes.Length / sizeof(float)];
        var codes = new int[codeBytes.Length / sizeof(int)];
        Buffer.BlockCopy(embeddingBytes, 0, embedding, 0, embeddingBytes.Length);
        Buffer.BlockCopy(codeBytes, 0, codes, 0, codeBytes.Length);
        var reference = new VoiceReference(embedding, codes, reader.GetInt32(offset + 10), reader.GetInt32(offset + 11),
            reader.GetString(offset + 7));
        return new(
            reader.GetString(offset), (VoiceProfileKind)reader.GetInt32(offset + 1), reader.GetString(offset + 2),
            reader.GetString(offset + 3), reader.IsDBNull(offset + 4) ? null : reader.GetInt32(offset + 4),
            reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5),
            reader.IsDBNull(offset + 6) ? null : reader.GetInt64(offset + 6), reference, reader.GetString(offset + 17),
            reader.IsDBNull(offset + 16) ? null : reader.GetString(offset + 16),
            reader.IsDBNull(offset + 12) ? null : reader.GetString(offset + 12),
            reader.IsDBNull(offset + 13) ? null : reader.GetInt32(offset + 13),
            reader.IsDBNull(offset + 14)
                ? (reader.IsDBNull(offset + 15) ? null : reader.GetString(offset + 15))
                : reader.GetString(offset + 14));
    }

    private static async Task<StoredVoiceProfile?> GetProfileById(
        SqliteConnection connection,
        string profileId,
        CancellationToken token,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null) command.Transaction = transaction;
        command.CommandText = """
            SELECT id,kind,language,model_hash,palette_version,design_instruction,seed,
                   ref_text,speaker_embedding,rvq_codes,rvq_length,codebooks,
                   domain_id,catalog_version,traits_json,variant_traits_json,
                   source_metadata,profile_hash,created_utc
            FROM voice_profile WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", profileId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadProfile(reader) : null;
    }

    private sealed record PoolCandidate(string ProfileId, string? SlotTraitsJson, long Sequence, int Score);

    private static string NormalizeSex(string sex)
    {
        var value = sex.Trim().ToLowerInvariant();
        if (value.Contains("femin", StringComparison.Ordinal) || value.Contains("female", StringComparison.Ordinal)) return "feminine";
        if (value.Contains("mascul", StringComparison.Ordinal) || value.Contains("male", StringComparison.Ordinal)) return "masculine";
        return value;
    }

    private static string LegacyDomain(uint territoryId) => $"legacy:{territoryId}";

    private static byte[] ToBytes<T>(T[] values) where T : struct
    {
        var result = new byte[values.Length * System.Runtime.InteropServices.Marshal.SizeOf<T>()];
        Buffer.BlockCopy(values, 0, result, 0, result.Length);
        return result;
    }
}
