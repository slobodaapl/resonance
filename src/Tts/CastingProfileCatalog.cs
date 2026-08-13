using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Resonance.Tts;

public enum CastingEvidenceSource
{
    Identity,
    Culture,
    Species,
    Faction,
    Territory,
    Generic,
}

public sealed record SpeakerCastingEvidence(
    string StableSpeakerKey,
    string? TerritoryPlaceName = null,
    string? FirstTerritoryPlaceName = null,
    uint? NpcBaseId = null,
    string? Sex = null,
    string? Category = null,
    string? Culture = null,
    string? Tribe = null,
    string? Species = null,
    string? Race = null,
    string? Faction = null,
    string? ModelChara = null,
    string? Age = null,
    string? Physique = null,
    string? BodyType = null,
    string? HeightBucket = null,
    string? MuscleMassBucket = null,
    string? Class = null,
    string? Personality = null,
    IReadOnlyList<string>? ModifierIds = null,
    int? RaceId = null,
    int? TribeId = null,
    int? BodyTypeId = null,
    int? HeightValue = null,
    long? ModelCharaId = null,
    int? ModelFamilyId = null,
    int? ModelType = null,
    int? ModelBase = null,
    int? ModelVariant = null,
    long? ModelHeadId = null,
    long? ModelBodyId = null,
    long? ModelHandsId = null,
    long? ModelLegsId = null,
    long? ModelFeetId = null);

public sealed record TerritoryCastingPrior(
    string DomainId,
    double Weight,
    IReadOnlyList<string> AllowedSpeakerCategories,
    IReadOnlyList<string> ModifierIds);

public sealed record TerritoryCastingProfile(
    string PlaceName,
    string Confidence,
    IReadOnlyList<TerritoryCastingPrior> Priors)
{
    public bool HasGeographicPrior => Priors.Count > 0;
}

public sealed record CastingSlotTemplate(
    string Id,
    string Sex,
    string Age,
    string Physique,
    string BodyType,
    string HeightBucket,
    string MuscleMassBucket,
    string Register,
    string Personality,
    string EnglishPrompt,
    string NeutralPrompt,
    string? JapanesePrompt = null,
    string? GermanPrompt = null,
    string? FrenchPrompt = null);

public sealed record CastingDomain(
    string Id,
    string Confidence,
    string FallbackDimensions,
    string EnglishPrompt,
    string NeutralPrompt,
    string? Inherits,
    IReadOnlyList<CastingSlotTemplate> MasculineSlots,
    IReadOnlyList<CastingSlotTemplate> FeminineSlots,
    string? JapanesePrompt = null,
    string? GermanPrompt = null,
    string? FrenchPrompt = null);

public sealed record CastingModifier(
    string Id,
    string EnglishPrompt,
    string NeutralPrompt,
    string? JapanesePrompt = null,
    string? GermanPrompt = null,
    string? FrenchPrompt = null);

public sealed record CastingIdentityGroup(
    string Id,
    IReadOnlyList<uint> NpcBaseIds,
    string DomainId,
    IReadOnlyList<string> ModifierIds,
    string Confidence);

public sealed record CastingRule(
    string Id,
    string Kind,
    string Value,
    string DomainId,
    IReadOnlyList<string> ModifierIds,
    IReadOnlyList<string> TerritoryPlaceNames,
    string? SpeakerCategory,
    int Priority,
    string Confidence);

public sealed record CastingMetadataMatch(
    IReadOnlyList<int> RaceIds,
    IReadOnlyList<int> TribeIds,
    IReadOnlyList<int> BodyTypeIds,
    IReadOnlyList<int> HeightValues,
    IReadOnlyList<long> ModelCharaIds,
    IReadOnlyList<int> ModelFamilyIds,
    IReadOnlyList<int> ModelTypes,
    IReadOnlyList<int> ModelBases,
    IReadOnlyList<int> ModelVariants,
    IReadOnlyList<long> ModelHeadIds,
    IReadOnlyList<long> ModelBodyIds,
    IReadOnlyList<long> ModelHandsIds,
    IReadOnlyList<long> ModelLegsIds,
    IReadOnlyList<long> ModelFeetIds);

public sealed record CastingMetadataRule(
    string Id,
    string DomainId,
    IReadOnlyList<string> ModifierIds,
    IReadOnlyList<string> TerritoryPlaceNames,
    string SpeakerCategory,
    int Priority,
    string Confidence,
    CastingEvidenceSource Source,
    string? VoiceSex,
    CastingMetadataMatch Match);

public sealed record CastingResolution(
    string DomainId,
    IReadOnlyList<string> ModifierIds,
    CastingEvidenceSource Source,
    string? TerritoryPlaceName,
    string? FirstTerritoryPlaceName,
    int CatalogVersion,
    bool UnknownTerritory,
    bool HasGeographicPrior,
    IReadOnlyList<TerritoryCastingPrior> TerritoryPriors,
    IReadOnlyList<string> CandidateDomainIds,
    IReadOnlyList<string> Diagnostics)
{
    public string SourceName => Source.ToString();
}

public sealed record CatalogValidationIssue(string Code, string Path, string Message)
{
    public override string ToString() => $"{Code} at {Path}: {Message}";
}

public sealed class CastingProfileCatalogException : FormatException
{
    public CastingProfileCatalogException(IReadOnlyList<CatalogValidationIssue> issues)
        : base(string.Join(Environment.NewLine, issues.Select(issue => issue.ToString())))
    {
        Issues = issues;
    }

    public IReadOnlyList<CatalogValidationIssue> Issues { get; }
}

/// <summary>
/// Strict, data-only casting catalog. Territory keys are the exact English
/// TerritoryType.PlaceName values supplied by the caller. This type deliberately
/// has no Lumina dependency: the live resolver owns conversion from a territory
/// row to its English place-name string.
/// </summary>
public sealed class CastingProfileCatalog
{
    public const int CurrentSchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private static readonly string[] ForbiddenNonEnglishAccentLabels =
    [
        "British",
        "Yorkshire",
        "General American",
        "American accent",
        "Indian English",
        "Indian accent",
        "South Asian",
        "West Country",
        "Cornish",
        "Scottish",
        "Icelandic",
        "Mongolian",
        "Russian accent",
    ];

    private readonly CatalogDocument document;
    private readonly ReadOnlyCollection<CastingDomain> domains;
    private readonly ReadOnlyCollection<CastingModifier> modifiers;
    private readonly ReadOnlyCollection<CastingIdentityGroup> identityGroups;
    private readonly ReadOnlyCollection<CastingRule> rules;
    private readonly ReadOnlyCollection<CastingMetadataRule> metadataRules;
    private readonly ReadOnlyCollection<TerritoryCastingProfile> territories;
    private readonly Dictionary<string, CastingDomain> domainsById;
    private readonly Dictionary<string, CastingModifier> modifiersById;
    private readonly Dictionary<string, TerritoryCastingProfile> territoriesByPlaceName;

    private CastingProfileCatalog(CatalogDocument document)
    {
        this.document = document;
        var slotTemplates = document.SlotTemplates!
            .Select(ToSlot)
            .ToDictionary(slot => slot.Id, StringComparer.Ordinal);
        domains = new(document.Domains!.Select(domain => ToDomain(domain, slotTemplates)).ToList());
        modifiers = new(document.Modifiers!.Select(ToModifier).ToList());
        identityGroups = new(document.IdentityGroups!.Select(ToIdentityGroup).ToList());
        rules = new(document.Rules!.Select(ToRule).ToList());
        metadataRules = new(document.MetadataRules!.Select(ToMetadataRule).ToList());
        territories = new(document.Territories!.Select(ToTerritory).ToList());
        domainsById = domains.ToDictionary(domain => domain.Id, StringComparer.Ordinal);
        modifiersById = modifiers.ToDictionary(modifier => modifier.Id, StringComparer.Ordinal);
        territoriesByPlaceName = territories.ToDictionary(territory => territory.PlaceName, StringComparer.Ordinal);
    }

    public int Version => document.Version;

    public string CatalogVersion => Version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string DefaultDomainId => document.DefaultDomain!;

    public IReadOnlyList<CastingDomain> Domains => domains;

    public IReadOnlyList<CastingModifier> Modifiers => modifiers;

    public IReadOnlyList<CastingIdentityGroup> IdentityGroups => identityGroups;

    public IReadOnlyList<CastingRule> Rules => rules;

    public IReadOnlyList<CastingMetadataRule> MetadataRules => metadataRules;

    public IReadOnlyList<TerritoryCastingProfile> Territories => territories;

    public static string InferVoiceAge(int bodyType, int height) =>
        height == byte.MaxValue && bodyType is 1 or 4
        || bodyType == 4 && height is >= 1 and <= 80
            ? "young"
            : "adult";

    public static CastingProfileCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadAllText(path));
    }

    public static CastingProfileCatalog Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        CatalogDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions)
                ?? throw new JsonException("Catalog document is empty");
        }
        catch (Exception error) when (IsCatalogShapeException(error))
        {
            throw new CastingProfileCatalogException(
                [new("malformed-schema", "$", error.Message)]);
        }

        var issues = ValidateDocument(document);
        if (issues.Count > 0) throw new CastingProfileCatalogException(issues);
        return new CastingProfileCatalog(document);
    }

    /// <summary>Returns validation issues without constructing a catalog.</summary>
    public static IReadOnlyList<CatalogValidationIssue> ValidateJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            var document = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions);
            return document is null
                ? [new("malformed-schema", "$", "Catalog document is empty")]
                : ValidateDocument(document);
        }
        catch (Exception error) when (IsCatalogShapeException(error))
        {
            return [new("malformed-schema", "$", error.Message)];
        }
    }

    public IReadOnlyList<CatalogValidationIssue> Validate() => ValidateDocument(document);

    private static bool IsCatalogShapeException(Exception error) =>
        error is JsonException
        or NotSupportedException
        or InvalidOperationException
        or OverflowException
        or FormatException;

    public bool TryGetTerritory(string placeName, out TerritoryCastingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(placeName);
        return territoriesByPlaceName.TryGetValue(placeName, out profile!);
    }

    public TerritoryCastingProfile GetTerritory(string placeName)
    {
        ArgumentNullException.ThrowIfNull(placeName);
        return territoriesByPlaceName.TryGetValue(placeName, out var profile)
            ? profile
            : new TerritoryCastingProfile(placeName, "C", []);
    }

    public IReadOnlyList<TerritoryCastingPrior> GetTerritoryPriors(string placeName)
    {
        ArgumentNullException.ThrowIfNull(placeName);
        return territoriesByPlaceName.TryGetValue(placeName, out var profile) ? profile.Priors : [];
    }

    /// <summary>
    /// Returns the active priors after speaker-category filtering, normalized
    /// to a sum of one. The source catalog keeps the documented relative
    /// weights (for example Tuliyollal's 30:15:10:10:10) unchanged.
    /// </summary>
    public IReadOnlyList<TerritoryCastingPrior> GetNormalizedTerritoryPriors(
        string placeName,
        SpeakerCastingEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(placeName);
        if (!territoriesByPlaceName.TryGetValue(placeName, out var profile)) return [];
        var eligible = profile.Priors.Where(prior => IsPriorAllowed(prior, evidence)).ToArray();
        var total = eligible.Sum(prior => prior.Weight);
        return total > 0 && double.IsFinite(total)
            ? eligible.Select(prior => prior with { Weight = prior.Weight / total }).ToArray()
            : [];
    }

    /// <summary>
    /// Returns exact territory-default modifiers for a winning domain. A
    /// modifier applies only when the territory prior names that same domain
    /// and its category filter accepts the speaker.
    /// </summary>
    public IReadOnlyList<string> GetApplicableTerritoryModifierIds(
        string? placeName,
        string domainId,
        SpeakerCastingEvidence? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainId);
        _ = GetDomain(domainId);
        if (placeName is null || !territoriesByPlaceName.TryGetValue(placeName, out var territory)) return [];
        return territory.Priors
            .Where(prior => String.Equals(prior.DomainId, domainId, StringComparison.Ordinal)
                && IsPriorAllowed(prior, evidence))
            .SelectMany(prior => prior.ModifierIds)
            .Where(modifierId => modifiersById.ContainsKey(modifierId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Returns territory candidates in configured order. Exact place-name
    /// matching is ordinal and never performs localization, substring, or
    /// fuzzy matching.
    /// </summary>
    public IReadOnlyList<CastingDomain> GetCandidateDomains(
        string? placeName,
        SpeakerCastingEvidence? evidence = null)
    {
        IReadOnlyList<TerritoryCastingPrior> territoryPriors = placeName is not null && territoriesByPlaceName.TryGetValue(placeName, out var territory)
            ? territory.Priors
            : [];
        var candidates = new List<CastingDomain>();

        void AddCandidate(string domainId)
        {
            if (!domainsById.TryGetValue(domainId, out var domain)) return;
            if (candidates.All(candidate => !String.Equals(candidate.Id, domain.Id, StringComparison.Ordinal)))
                candidates.Add(domain);
        }

        if (evidence is not null)
        {
            foreach (var rule in MatchingDomainRules(evidence, placeName)) AddCandidate(rule.DomainId);
            if (TryFindIdentity(evidence, out var identity)) AddCandidate(identity.DomainId);
        }

        foreach (var prior in territoryPriors)
        {
            if (!IsPriorAllowed(prior, evidence)) continue;
            AddCandidate(prior.DomainId);
        }

        if (candidates.Count == 0 && domainsById.TryGetValue(DefaultDomainId, out var fallback))
            candidates.Add(fallback);
        return candidates;
    }

    public CastingResolution Resolve(SpeakerCastingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return Resolve(evidence.TerritoryPlaceName, evidence);
    }

    public CastingResolution Resolve(string? territoryPlaceName, SpeakerCastingEvidence? evidence = null)
    {
        evidence ??= new SpeakerCastingEvidence(string.Empty, territoryPlaceName, territoryPlaceName);
        var firstTerritory = String.IsNullOrEmpty(evidence.FirstTerritoryPlaceName)
            ? territoryPlaceName
            : evidence.FirstTerritoryPlaceName;
        var diagnostics = new List<string>();
        var territoryKnown = territoryPlaceName is not null && territoriesByPlaceName.ContainsKey(territoryPlaceName);
        IReadOnlyList<TerritoryCastingPrior> territoryPriors = territoryKnown
            ? territoriesByPlaceName[territoryPlaceName!].Priors
            : [];
        var unknownTerritory = territoryPlaceName is not null && !territoryKnown;
        if (unknownTerritory) diagnostics.Add("unknown-territory");
        if (territoryKnown && territoryPriors.Count == 0) diagnostics.Add("territory-has-no-geographic-domain");

        var modifiers = new List<string>();
        string domainId;
        CastingEvidenceSource source;
        var hasGeographicPrior = territoryPriors.Count > 0;

        if (TryFindRule(evidence, territoryPlaceName, out var rule, out source))
        {
            domainId = rule.DomainId;
            AddModifiers(modifiers, rule.ModifierIds);
        }
        else if (TryFindIdentity(evidence, out var identity))
        {
            domainId = identity.DomainId;
            source = CastingEvidenceSource.Identity;
            AddModifiers(modifiers, identity.ModifierIds);
        }
        else
        {
            var eligiblePriors = territoryPriors.Where(prior => IsPriorAllowed(prior, evidence)).ToList();
            if (eligiblePriors.Count > 0)
            {
                domainId = SelectWeightedDomain(eligiblePriors, evidence.StableSpeakerKey, firstTerritory);
                source = CastingEvidenceSource.Territory;
            }
            else
            {
                domainId = DefaultDomainId;
                source = CastingEvidenceSource.Generic;
                if (territoryKnown && territoryPriors.Count > 0)
                    diagnostics.Add("territory-priors-filtered");
            }
        }

        if (!domainsById.ContainsKey(domainId))
        {
            diagnostics.Add("resolved-domain-missing");
            domainId = DefaultDomainId;
            source = CastingEvidenceSource.Generic;
        }

        AddModifiers(modifiers, GetApplicableTerritoryModifierIds(territoryPlaceName, domainId, evidence));
        AddModifiers(modifiers, evidence.ModifierIds ?? []);
        AddTraitModifiers(modifiers, evidence, territoryPlaceName, domainId);

        var candidates = GetCandidateDomains(territoryPlaceName, evidence)
            .Select(candidate => candidate.Id)
            .ToArray();
        if (candidates.Length == 0) candidates = [DefaultDomainId];
        return new CastingResolution(
            domainId,
            modifiers.AsReadOnly(),
            source,
            territoryPlaceName,
            firstTerritory,
            Version,
            unknownTerritory,
            hasGeographicPrior,
            territoryPriors,
            candidates,
            diagnostics.AsReadOnly());
    }

    public CastingSlotTemplate SelectBestSlot(
        CastingResolution resolution,
        SpeakerCastingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(evidence);
        var domain = GetDomain(resolution.DomainId);
        var slots = SlotsForSex(domain, evidence.Sex);
        IReadOnlyList<CastingSlotTemplate> eligibleSlots = HasExplicitAge(evidence)
            ? slots
            : slots.Where(IsAdultSlot).ToArray();
        if (eligibleSlots.Count == 0) eligibleSlots = slots;
        if (!HasKnownSlotTraits(evidence))
            return eligibleSlots.Where(IsNeutralSlot).OrderBy(slot => slot.Id, StringComparer.Ordinal).FirstOrDefault()
                ?? eligibleSlots.OrderBy(slot => slot.Id, StringComparer.Ordinal).First();
        return eligibleSlots
            .OrderByDescending(slot => ScoreSlot(slot, evidence))
            .ThenByDescending(slot => IsNeutralSlot(slot))
            .ThenBy(slot => slot.Id, StringComparer.Ordinal)
            .First();
    }

    public string FallbackVariantId(CastingResolution resolution, SpeakerCastingEvidence evidence)
    {
        var dimensions = GetDomain(resolution.DomainId).FallbackDimensions;
        var sex = NormalizeSex(evidence.Sex);
        var age = String.Equals(evidence.Age, "young", StringComparison.Ordinal) ? "young" : "adult";
        return dimensions switch
        {
            "none" => "default",
            "feminine_only" => "feminine",
            "sex" => sex,
            "sex_age" => $"{sex}_{age}",
            _ => throw new InvalidDataException($"Unknown fallback dimensions '{dimensions}'"),
        };
    }

    public string ResolveVoiceSex(SpeakerCastingEvidence evidence, string fallbackSex)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var rule = metadataRules
            .Where(candidate => candidate.VoiceSex is not null)
            .Where(candidate => Matches(candidate, evidence, evidence.TerritoryPlaceName))
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return rule?.VoiceSex ?? NormalizeSex(fallbackSex);
    }

    public CastingSlotTemplate GetSlot(string domainId, string sex, string slotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainId);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);
        var slots = SlotsForSex(GetDomain(domainId), sex);
        return slots.FirstOrDefault(slot => String.Equals(slot.Id, slotId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Unknown {sex} slot '{slotId}' in domain '{domainId}'");
    }

    public int ScoreSlot(CastingSlotTemplate slot, SpeakerCastingEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(slot);
        ArgumentNullException.ThrowIfNull(evidence);
        var score = 0;
        score += ExactTraitScore(slot.Age, evidence.Age, 5);
        score += ExactTraitScore(slot.Physique, evidence.Physique, 4);
        score += ExactTraitScore(slot.BodyType, evidence.BodyType, 3);
        score += ExactTraitScore(slot.HeightBucket, evidence.HeightBucket, 2);
        score += ExactTraitScore(slot.MuscleMassBucket, evidence.MuscleMassBucket, 2);
        score += ExactTraitScore(slot.Register, evidence.Class, 2);
        score += ExactTraitScore(slot.Personality, evidence.Personality, 1);
        return score;
    }

    public string BuildPrompt(
        CastingResolution resolution,
        string language,
        string sex,
        string? slotId = null,
        SpeakerCastingEvidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var languageKey = NormalizeLanguage(language);
        var domain = GetDomain(resolution.DomainId);
        var requestedSex = NormalizeSex(sex);
        var slots = SlotsForSex(domain, requestedSex);
        var selectionEvidence = evidence is null
            ? new SpeakerCastingEvidence(string.Empty, Sex: requestedSex)
            : evidence with { Sex = requestedSex };
        var slot = slotId is null
            ? SelectBestSlot(resolution, selectionEvidence)
            : slots.FirstOrDefault(candidate => String.Equals(candidate.Id, slotId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"Unknown {requestedSex} slot '{slotId}' in domain '{domain.Id}'");

        var parts = new List<string>
        {
            PromptFor(domain.EnglishPrompt, domain.NeutralPrompt, languageKey,
                domain.JapanesePrompt, domain.GermanPrompt, domain.FrenchPrompt),
            PromptFor(slot.EnglishPrompt, slot.NeutralPrompt, languageKey,
                slot.JapanesePrompt, slot.GermanPrompt, slot.FrenchPrompt),
        };
        foreach (var modifierId in resolution.ModifierIds.Distinct(StringComparer.Ordinal))
        {
            if (!modifiersById.TryGetValue(modifierId, out var modifier)) continue;
            parts.Add(PromptFor(modifier.EnglishPrompt, modifier.NeutralPrompt, languageKey,
                modifier.JapanesePrompt, modifier.GermanPrompt, modifier.FrenchPrompt));
        }

        var body = string.Join(" ", parts.Where(part => !String.IsNullOrWhiteSpace(part)));
        if (!String.Equals(languageKey, "english", StringComparison.Ordinal))
            body = NeutralizeEnglishAccentLabels(body);
        return $"Speak the supplied dialogue in {LanguageName(languageKey)}. {body} Clear dialogue; emotionally responsive; no caricature or celebrity imitation.";
    }

    /// <summary>
    /// Returns the stable prior-selection seed. Language is accepted only so
    /// callers can prove it is ignored; it is intentionally absent from the
    /// hashed input.
    /// </summary>
    public ulong GetDeterministicSelectionSeed(
        string stableSpeakerKey,
        string? firstTerritoryPlaceName,
        string? language = null)
    {
        ArgumentNullException.ThrowIfNull(stableSpeakerKey);
        _ = language;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{stableSpeakerKey}\0{firstTerritoryPlaceName ?? string.Empty}\0{Version}"));
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    public CastingDomain GetDomain(string domainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domainId);
        return domainsById.TryGetValue(domainId, out var domain)
            ? domain
            : throw new KeyNotFoundException($"Unknown casting domain '{domainId}'");
    }

    private bool TryFindIdentity(SpeakerCastingEvidence evidence, out CastingIdentityGroup identity)
    {
        identity = null!;
        if (evidence.NpcBaseId is not uint npcBaseId) return false;
        identity = identityGroups.FirstOrDefault(group => group.NpcBaseIds.Contains(npcBaseId))!;
        return identity is not null;
    }

    private bool TryFindRule(
        SpeakerCastingEvidence evidence,
        string? territoryPlaceName,
        out CastingRule rule,
        out CastingEvidenceSource source)
    {
        rule = null!;
        source = CastingEvidenceSource.Generic;
        var matches = MatchingDomainRules(evidence, territoryPlaceName).ToList();
        if (matches.Count == 0) return false;
        rule = matches[0];
        source = SourceForKind(rule.Kind);
        return true;
    }

    private IEnumerable<CastingRule> MatchingDomainRules(
        SpeakerCastingEvidence evidence,
        string? territoryPlaceName)
    {
        var semantic = rules
            .Where(candidate => DomainRuleKinds.Contains(candidate.Kind))
            .Where(candidate => Matches(candidate, evidence, territoryPlaceName));
        var derived = metadataRules
            .Where(candidate => Matches(candidate, evidence, territoryPlaceName))
            .Select(candidate => new CastingRule(
                candidate.Id, MetadataRuleKind(candidate.Source), candidate.Id, candidate.DomainId,
                candidate.ModifierIds, candidate.TerritoryPlaceNames, candidate.SpeakerCategory,
                candidate.Priority, candidate.Confidence));
        return semantic.Concat(derived)
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => RuleKindRank(candidate.Kind))
            .ThenBy(candidate => candidate.Id, StringComparer.Ordinal);
    }

    private static string MetadataRuleKind(CastingEvidenceSource source) => source switch
    {
        CastingEvidenceSource.Faction => "faction",
        CastingEvidenceSource.Culture => "culture",
        _ => "model",
    };

    private static bool Matches(
        CastingMetadataRule rule,
        SpeakerCastingEvidence evidence,
        string? territoryPlaceName)
    {
        if (rule.TerritoryPlaceNames.Count > 0 &&
            (territoryPlaceName is null || !rule.TerritoryPlaceNames.Contains(territoryPlaceName, StringComparer.Ordinal)))
            return false;
        if (!CategoryAllowed(rule.SpeakerCategory, evidence.Category)) return false;
        var match = rule.Match;
        return Matches(match.RaceIds, evidence.RaceId)
            && Matches(match.TribeIds, evidence.TribeId)
            && Matches(match.BodyTypeIds, evidence.BodyTypeId)
            && Matches(match.HeightValues, evidence.HeightValue)
            && Matches(match.ModelCharaIds, evidence.ModelCharaId)
            && Matches(match.ModelFamilyIds, evidence.ModelFamilyId)
            && Matches(match.ModelTypes, evidence.ModelType)
            && Matches(match.ModelBases, evidence.ModelBase)
            && Matches(match.ModelVariants, evidence.ModelVariant)
            && Matches(match.ModelHeadIds, evidence.ModelHeadId)
            && Matches(match.ModelBodyIds, evidence.ModelBodyId)
            && Matches(match.ModelHandsIds, evidence.ModelHandsId)
            && Matches(match.ModelLegsIds, evidence.ModelLegsId)
            && Matches(match.ModelFeetIds, evidence.ModelFeetId);
    }

    private static bool Matches<T>(IReadOnlyList<T> expected, T? actual) where T : struct =>
        expected.Count == 0 || actual is { } value && expected.Contains(value);

    private static bool Matches(CastingRule rule, SpeakerCastingEvidence evidence, string? territoryPlaceName)
    {
        if (rule.TerritoryPlaceNames.Count > 0 &&
            (territoryPlaceName is null || !rule.TerritoryPlaceNames.Contains(territoryPlaceName, StringComparer.Ordinal)))
            return false;
        if (!CategoryAllowed(rule.SpeakerCategory, evidence.Category)) return false;
        var actual = rule.Kind switch
        {
            "culture" => evidence.Culture,
            "tribe" => evidence.Tribe,
            "species" => evidence.Species,
            "race" => evidence.Race,
            "model" => evidence.ModelChara,
            "faction" => evidence.Faction,
            "class" => evidence.Class,
            "personality" => evidence.Personality,
            _ => null,
        };
        return actual is not null && String.Equals(actual, rule.Value, StringComparison.Ordinal);
    }

    private static bool CategoryAllowed(string? requiredCategory, string? actualCategory)
    {
        if (requiredCategory is null || String.Equals(requiredCategory, "any", StringComparison.Ordinal)) return true;
        if (String.Equals(requiredCategory, "non_humanoid", StringComparison.Ordinal))
            return String.Equals(actualCategory, "non_humanoid", StringComparison.Ordinal);
        if (String.Equals(requiredCategory, "humanoid", StringComparison.Ordinal))
            return String.Equals(actualCategory, "humanoid", StringComparison.Ordinal);
        if (actualCategory is null) return false;
        return String.Equals(requiredCategory, actualCategory, StringComparison.Ordinal);
    }

    private static CastingEvidenceSource SourceForKind(string kind) => kind switch
    {
        "species" or "race" or "model" => CastingEvidenceSource.Species,
        "faction" => CastingEvidenceSource.Faction,
        _ => CastingEvidenceSource.Culture,
    };

    private static int RuleKindRank(string kind) => kind switch
    {
        "tribe" => 0,
        "species" => 1,
        "culture" => 2,
        "faction" => 3,
        "model" => 4,
        "race" => 5,
        "class" => 6,
        "personality" => 7,
        _ => 100,
    };

    private static bool IsPriorAllowed(TerritoryCastingPrior prior, SpeakerCastingEvidence? evidence)
    {
        if (prior.AllowedSpeakerCategories.Count == 0) return true;
        if (evidence?.Category is null) return false;
        return prior.AllowedSpeakerCategories.Contains(evidence.Category, StringComparer.Ordinal);
    }

    private string SelectWeightedDomain(
        IReadOnlyList<TerritoryCastingPrior> priors,
        string stableSpeakerKey,
        string? firstTerritoryPlaceName)
    {
        var total = priors.Sum(prior => prior.Weight);
        if (!(total > 0) || !double.IsFinite(total)) return priors[0].DomainId;
        var value = GetDeterministicSelectionSeed(stableSpeakerKey, firstTerritoryPlaceName);
        var unit = value / ((double)ulong.MaxValue + 1d);
        var selected = unit * total;
        foreach (var prior in priors)
        {
            selected -= prior.Weight;
            if (selected < 0) return prior.DomainId;
        }
        return priors[^1].DomainId;
    }

    private void AddModifiers(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
            if (modifiersById.ContainsKey(value) && !target.Contains(value, StringComparer.Ordinal)) target.Add(value);
    }

    private void AddTraitModifiers(
        List<string> target,
        SpeakerCastingEvidence evidence,
        string? territoryPlaceName,
        string domainId)
    {
        foreach (var rule in rules
                     .Where(candidate => TraitRuleKinds.Contains(candidate.Kind)
                         && String.Equals(candidate.DomainId, domainId, StringComparison.Ordinal)
                         && Matches(candidate, evidence, territoryPlaceName))
                     .OrderByDescending(candidate => candidate.Priority)
                     .ThenBy(candidate => candidate.Id, StringComparer.Ordinal))
            AddModifiers(target, rule.ModifierIds);
    }

    private static int ExactTraitScore(string expected, string? actual, int points) =>
        actual is not null && String.Equals(expected, actual, StringComparison.Ordinal) ? points : 0;

    private static bool HasKnownSlotTraits(SpeakerCastingEvidence evidence) =>
        HasExplicitAge(evidence)
        || !String.IsNullOrWhiteSpace(evidence.Physique)
        || !String.IsNullOrWhiteSpace(evidence.BodyType)
        || !String.IsNullOrWhiteSpace(evidence.HeightBucket)
        || !String.IsNullOrWhiteSpace(evidence.MuscleMassBucket)
        || !String.IsNullOrWhiteSpace(evidence.Class)
        || !String.IsNullOrWhiteSpace(evidence.Personality);

    private static bool HasExplicitAge(SpeakerCastingEvidence evidence) =>
        evidence.Age is "young" or "adult" or "elder";

    private static bool IsAdultSlot(CastingSlotTemplate slot) =>
        String.Equals(slot.Age, "adult", StringComparison.Ordinal);

    private static bool IsNeutralSlot(CastingSlotTemplate slot) =>
        String.Equals(slot.Age, "adult", StringComparison.Ordinal)
        && String.Equals(slot.Physique, "average", StringComparison.Ordinal)
        && String.Equals(slot.Register, "grounded", StringComparison.Ordinal)
        && String.Equals(slot.Personality, "steady", StringComparison.Ordinal);

    private static IReadOnlyList<CastingSlotTemplate> SlotsForSex(CastingDomain domain, string? sex)
    {
        var normalized = NormalizeSex(sex);
        return String.Equals(normalized, "feminine", StringComparison.Ordinal)
            ? domain.FeminineSlots
            : domain.MasculineSlots;
    }

    private static string NormalizeSex(string? sex) => sex?.ToLowerInvariant() switch
    {
        "f" or "female" or "feminine" => "feminine",
        "m" or "male" or "masculine" => "masculine",
        _ => "masculine",
    };

    private static string NormalizeLanguage(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        return language.Trim().ToLowerInvariant() switch
        {
            "en" or "eng" or "english" => "english",
            "ja" or "jpn" or "japanese" => "japanese",
            "de" or "deu" or "german" => "german",
            "fr" or "fra" or "french" => "french",
            _ => throw new NotSupportedException($"FFXIV dubbing language '{language}' is not supported"),
        };
    }

    private static string LanguageName(string language) => language switch
    {
        "english" => "English",
        "japanese" => "Japanese",
        "german" => "German",
        "french" => "French",
        _ => language,
    };

    private static string PromptFor(
        string english,
        string neutral,
        string language,
        string? japanese = null,
        string? german = null,
        string? french = null) => language switch
    {
        "english" => english,
        "japanese" when !String.IsNullOrWhiteSpace(japanese) => japanese,
        "german" when !String.IsNullOrWhiteSpace(german) => german,
        "french" when !String.IsNullOrWhiteSpace(french) => french,
        _ => neutral,
    };

    private static string NeutralizeEnglishAccentLabels(string text)
    {
        foreach (var label in ForbiddenNonEnglishAccentLabels)
            text = text.Replace(label, string.Empty, StringComparison.OrdinalIgnoreCase);
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static CastingSlotTemplate ToSlot(SlotTemplateDto dto) => new(
        dto.Id!, dto.Sex!, dto.Age!, dto.Physique!, dto.BodyType!, dto.HeightBucket!,
        dto.MuscleMassBucket!, dto.Register!, dto.Personality!, dto.Prompts!.English!, dto.Prompts.Neutral!,
        dto.Prompts.Japanese, dto.Prompts.German, dto.Prompts.French);

    private static CastingModifier ToModifier(ModifierDto dto) => new(
        dto.Id!, dto.Prompts!.English!, dto.Prompts.Neutral!, dto.Prompts.Japanese, dto.Prompts.German,
        dto.Prompts.French);

    private static CastingIdentityGroup ToIdentityGroup(IdentityGroupDto dto) => new(
        dto.Id!, dto.NpcBaseIds!, dto.DomainId!, dto.ModifierIds ?? [], dto.Confidence!);

    private static CastingRule ToRule(RuleDto dto) => new(
        dto.Id!, dto.Kind!, dto.Value!, dto.DomainId!, dto.ModifierIds ?? [],
        dto.TerritoryPlaceNames ?? [], dto.SpeakerCategory, dto.Priority, dto.Confidence!);

    private static CastingMetadataRule ToMetadataRule(MetadataRuleDto dto) => new(
        dto.Id!, dto.DomainId!, dto.ModifierIds ?? [], dto.TerritoryPlaceNames ?? [],
        dto.SpeakerCategory ?? "any", dto.Priority, dto.Confidence!,
        Enum.Parse<CastingEvidenceSource>(dto.Source!, ignoreCase: true),
        dto.VoiceSex,
        new CastingMetadataMatch(
            dto.Match!.RaceIds ?? [], dto.Match.TribeIds ?? [], dto.Match.BodyTypeIds ?? [],
            dto.Match.HeightValues ?? [], dto.Match.ModelCharaIds ?? [], dto.Match.ModelFamilyIds ?? [],
            dto.Match.ModelTypes ?? [], dto.Match.ModelBases ?? [], dto.Match.ModelVariants ?? [],
            dto.Match.ModelHeadIds ?? [], dto.Match.ModelBodyIds ?? [], dto.Match.ModelHandsIds ?? [],
            dto.Match.ModelLegsIds ?? [], dto.Match.ModelFeetIds ?? []));

    private static TerritoryCastingProfile ToTerritory(TerritoryDto dto) => new(
        dto.PlaceName!, dto.Confidence!, (dto.Priors ?? []).Select(prior => new TerritoryCastingPrior(
            prior.DomainId!, prior.Weight, prior.AllowedSpeakerCategories ?? [], prior.ModifierIds ?? [])).ToArray());

    private static IReadOnlyList<CatalogValidationIssue> ValidateDocument(CatalogDocument document)
    {
        var issues = new List<CatalogValidationIssue>();
        if (document.Version != CurrentSchemaVersion)
            issues.Add(new("unsupported-version", "version", $"Expected {CurrentSchemaVersion}, got {document.Version}"));
        if (string.IsNullOrWhiteSpace(document.DefaultDomain))
            issues.Add(new("missing-default-domain", "defaultDomain", "Default domain is required"));
        if (document.SlotTemplates is null)
            issues.Add(new("missing-slot-templates", "slotTemplates", "Slot template collection is required"));
        if (document.Domains is null)
            issues.Add(new("missing-domains", "domains", "Domain collection is required"));
        if (document.Modifiers is null)
            issues.Add(new("missing-modifiers", "modifiers", "Modifier collection is required"));
        if (document.IdentityGroups is null)
            issues.Add(new("missing-identity-groups", "identityGroups", "Identity group collection is required"));
        if (document.Rules is null)
            issues.Add(new("missing-rules", "rules", "Rule collection is required"));
        if (document.MetadataRules is null)
            issues.Add(new("missing-metadata-rules", "metadataRules", "Metadata rule collection is required"));
        if (document.Territories is null)
            issues.Add(new("missing-territories", "territories", "Territory collection is required"));
        if (issues.Count > 0) return issues;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var slotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (slot, index) in document.SlotTemplates!.Select((value, index) => (value, index)))
        {
            var path = $"slotTemplates[{index}]";
            if (slot is null)
            {
                issues.Add(new("null-entry", path, "Slot template entry cannot be null"));
                continue;
            }
            ValidateId(slot.Id, path, ids, issues);
            if (slotIds.Add(slot.Id ?? string.Empty) is false)
                issues.Add(new("duplicate-id", $"{path}.id", $"Duplicate slot template '{slot.Id}'"));
            ValidatePrompt(slot.Prompts, $"{path}.prompts", issues);
            if (string.IsNullOrWhiteSpace(slot.Sex) || (slot.Sex is not ("masculine" or "feminine")))
                issues.Add(new("malformed-slot", $"{path}.sex", "Slot sex must be masculine or feminine"));
            ValidateSlotField(slot.Age, path, "age", issues);
            ValidateSlotField(slot.Physique, path, "physique", issues);
            ValidateSlotField(slot.BodyType, path, "bodyType", issues);
            ValidateSlotField(slot.HeightBucket, path, "heightBucket", issues);
            ValidateSlotField(slot.MuscleMassBucket, path, "muscleMassBucket", issues);
            ValidateSlotField(slot.Register, path, "register", issues);
            ValidateSlotField(slot.Personality, path, "personality", issues);
        }

        var slotLookup = document.SlotTemplates!
            .Where(slot => slot is not null && !string.IsNullOrWhiteSpace(slot.Id))
            .Select(slot => slot!)
            .GroupBy(slot => slot.Id!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var domainIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (domain, index) in document.Domains!.Select((value, index) => (value, index)))
        {
            var path = $"domains[{index}]";
            if (domain is null)
            {
                issues.Add(new("null-entry", path, "Domain entry cannot be null"));
                continue;
            }
            ValidateId(domain.Id, path, ids, issues);
            if (domain.Id is not null && !domainIds.Add(domain.Id))
                issues.Add(new("duplicate-id", $"{path}.id", $"Duplicate domain '{domain.Id}'"));
            ValidateConfidence(domain.Confidence, $"{path}.confidence", issues);
            ValidatePrompt(domain.Prompts, $"{path}.prompts", issues);
            if (domain.FallbackDimensions is not ("sex_age" or "sex" or "none" or "feminine_only"))
                issues.Add(new("invalid-fallback-dimensions", $"{path}.fallbackDimensions",
                    "Fallback dimensions must be sex_age, sex, none, or feminine_only"));
            if (domain.SlotTemplates is null)
            {
                issues.Add(new("missing-slot-templates", $"{path}.slotTemplates", "Domain slot templates are required"));
            }
            else
            {
                ValidateSlotReferences(domain.SlotTemplates.Masculine, "masculine", path, slotLookup, issues);
                ValidateSlotReferences(domain.SlotTemplates.Feminine, "feminine", path, slotLookup, issues);
            }
            if (!string.IsNullOrWhiteSpace(domain.Inherits) && domain.Inherits == domain.Id)
                issues.Add(new("reference-cycle", $"{path}.inherits", "Domain cannot inherit itself"));
        }

        if (document.DefaultDomain is not null && !domainIds.Contains(document.DefaultDomain))
            issues.Add(new("missing-reference", "defaultDomain", $"Unknown domain '{document.DefaultDomain}'"));
        ValidateInheritanceCycles(document.Domains!, domainIds, issues);

        var modifierIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (modifier, index) in document.Modifiers!.Select((value, index) => (value, index)))
        {
            var path = $"modifiers[{index}]";
            if (modifier is null)
            {
                issues.Add(new("null-entry", path, "Modifier entry cannot be null"));
                continue;
            }
            ValidateId(modifier.Id, path, ids, issues);
            if (modifier.Id is not null && !modifierIds.Add(modifier.Id))
                issues.Add(new("duplicate-id", $"{path}.id", $"Duplicate modifier '{modifier.Id}'"));
            ValidatePrompt(modifier.Prompts, $"{path}.prompts", issues);
        }

        var territoryNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (territory, index) in document.Territories!.Select((value, index) => (value, index)))
        {
            var path = $"territories[{index}]";
            if (territory is null)
            {
                issues.Add(new("null-entry", path, "Territory entry cannot be null"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(territory.PlaceName))
                issues.Add(new("malformed-territory", $"{path}.placeName", "Exact English place name is required"));
            else if (!territoryNames.Add(territory.PlaceName))
                issues.Add(new("duplicate-territory", $"{path}.placeName", $"Duplicate exact place name '{territory.PlaceName}'"));
            ValidateConfidence(territory.Confidence, $"{path}.confidence", issues);
            if (String.Equals(territory.Confidence, "Ø", StringComparison.Ordinal)
                && territory.Priors is { Count: > 0 })
                issues.Add(new(
                    "forbidden-geographic-prior",
                    $"{path}.priors",
                    "Territories with Ø confidence cannot define geographic priors"));
            var priorDomains = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (prior, priorIndex) in (territory.Priors ?? []).Select((value, index) => (value, index)))
            {
                var priorPath = $"{path}.priors[{priorIndex}]";
                if (prior is null)
                {
                    issues.Add(new("null-entry", priorPath, "Territory prior entry cannot be null"));
                    continue;
                }
                if (string.IsNullOrWhiteSpace(prior.DomainId) || !domainIds.Contains(prior.DomainId))
                    issues.Add(new("missing-reference", $"{priorPath}.domainId", $"Unknown domain '{prior.DomainId}'"));
                if (!priorDomains.Add(prior.DomainId ?? string.Empty))
                    issues.Add(new("duplicate-prior", $"{priorPath}.domainId", $"Domain appears more than once"));
                if (!(prior.Weight > 0) || !double.IsFinite(prior.Weight))
                    issues.Add(new("invalid-weight", $"{priorPath}.weight", "Weight must be finite and positive"));
                foreach (var category in prior.AllowedSpeakerCategories ?? [])
                    if (category is null || !SpeakerCategories.Contains(category))
                        issues.Add(new("malformed-prior", $"{priorPath}.allowedSpeakerCategories", $"Unsupported speaker category '{category}'"));
                ValidateModifierReferences(prior.ModifierIds, priorPath, modifierIds, issues);
            }
        }

        var npcIds = new HashSet<uint>();
        foreach (var (identity, index) in document.IdentityGroups!.Select((value, index) => (value, index)))
        {
            var path = $"identityGroups[{index}]";
            if (identity is null)
            {
                issues.Add(new("null-entry", path, "Identity group entry cannot be null"));
                continue;
            }
            ValidateId(identity.Id, path, ids, issues);
            ValidateConfidence(identity.Confidence, $"{path}.confidence", issues);
            if (identity.NpcBaseIds is null || identity.NpcBaseIds.Count == 0)
                issues.Add(new("malformed-identity", $"{path}.npcBaseIds", "At least one NpcBaseId is required"));
            else
            {
                foreach (var npcBaseId in identity.NpcBaseIds)
                {
                    if (npcBaseId == 0) issues.Add(new("malformed-identity", $"{path}.npcBaseIds", "NpcBaseId must be positive"));
                    if (!npcIds.Add(npcBaseId)) issues.Add(new("duplicate-identity", $"{path}.npcBaseIds", $"NpcBaseId {npcBaseId} is repeated"));
                }
            }
            if (identity.DomainId is null || !domainIds.Contains(identity.DomainId))
                issues.Add(new("missing-reference", $"{path}.domainId", $"Unknown domain '{identity.DomainId}'"));
            ValidateModifierReferences(identity.ModifierIds, path, modifierIds, issues);
        }

        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rule, index) in document.Rules!.Select((value, index) => (value, index)))
        {
            var path = $"rules[{index}]";
            if (rule is null)
            {
                issues.Add(new("null-entry", path, "Rule entry cannot be null"));
                continue;
            }
            ValidateId(rule.Id, path, ids, issues);
            if (rule.Id is not null && !ruleIds.Add(rule.Id))
                issues.Add(new("duplicate-id", $"{path}.id", $"Duplicate rule '{rule.Id}'"));
            if (rule.Kind is null || !RuleKinds.Contains(rule.Kind))
                issues.Add(new("malformed-rule", $"{path}.kind", "Unsupported rule kind"));
            if (string.IsNullOrWhiteSpace(rule.Value))
                issues.Add(new("malformed-rule", $"{path}.value", "Rule value is required"));
            if (rule.DomainId is null || !domainIds.Contains(rule.DomainId))
                issues.Add(new("missing-reference", $"{path}.domainId", $"Unknown domain '{rule.DomainId}'"));
            ValidateModifierReferences(rule.ModifierIds, path, modifierIds, issues);
            ValidateConfidence(rule.Confidence, $"{path}.confidence", issues);
            if (rule.SpeakerCategory is not null && !SpeakerCategories.Contains(rule.SpeakerCategory))
                issues.Add(new("malformed-rule", $"{path}.speakerCategory", "Unsupported speaker category"));
            if (String.Equals(rule.Kind, "species", StringComparison.Ordinal)
                && !String.Equals(rule.SpeakerCategory, "non_humanoid", StringComparison.Ordinal))
                issues.Add(new("malformed-rule", $"{path}.speakerCategory", "Species rules require non_humanoid category restriction"));
            if (rule.TerritoryPlaceNames is not null)
                foreach (var placeName in rule.TerritoryPlaceNames)
                    if (placeName is null || !territoryNames.Contains(placeName))
                        issues.Add(new("missing-reference", $"{path}.territoryPlaceNames", $"Unknown exact place name '{placeName}'"));
        }

        foreach (var (rule, index) in document.MetadataRules!.Select((value, index) => (value, index)))
        {
            var path = $"metadataRules[{index}]";
            if (rule is null)
            {
                issues.Add(new("null-entry", path, "Metadata rule entry cannot be null"));
                continue;
            }
            ValidateId(rule.Id, path, ids, issues);
            if (rule.DomainId is null || !domainIds.Contains(rule.DomainId))
                issues.Add(new("missing-reference", $"{path}.domainId", $"Unknown domain '{rule.DomainId}'"));
            ValidateModifierReferences(rule.ModifierIds, path, modifierIds, issues);
            ValidateConfidence(rule.Confidence, $"{path}.confidence", issues);
            if (rule.Source is not ("culture" or "species" or "faction"))
                issues.Add(new("malformed-metadata-rule", $"{path}.source", "Source must be culture, species or faction"));
            if (rule.VoiceSex is not null && rule.VoiceSex is not ("masculine" or "feminine"))
                issues.Add(new("malformed-metadata-rule", $"{path}.voiceSex", "Voice sex must be masculine or feminine"));
            if (rule.SpeakerCategory is not null && !SpeakerCategories.Contains(rule.SpeakerCategory))
                issues.Add(new("malformed-metadata-rule", $"{path}.speakerCategory", "Unsupported speaker category"));
            if (rule.Match is null || !rule.Match.HasConstraint)
                issues.Add(new("malformed-metadata-rule", $"{path}.match", "At least one raw metadata constraint is required"));
            if (rule.TerritoryPlaceNames is not null)
                foreach (var placeName in rule.TerritoryPlaceNames)
                    if (placeName is null || !territoryNames.Contains(placeName))
                        issues.Add(new("missing-reference", $"{path}.territoryPlaceNames", $"Unknown exact place name '{placeName}'"));
        }

        return issues;
    }

    private static readonly HashSet<string> RuleKinds = new(StringComparer.Ordinal)
    {
        "culture", "tribe", "species", "race", "model", "faction", "class", "personality",
    };

    private static readonly HashSet<string> DomainRuleKinds = new(StringComparer.Ordinal)
    {
        "culture", "tribe", "species", "race", "model", "faction",
    };

    private static readonly HashSet<string> TraitRuleKinds = new(StringComparer.Ordinal)
    {
        "class", "personality",
    };

    private static readonly HashSet<string> SpeakerCategories = new(StringComparer.Ordinal)
    {
        "any", "humanoid", "non_humanoid",
    };

    private static void ValidateId(string? value, string path, HashSet<string> ids, List<CatalogValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new("missing-id", $"{path}.id", "ID is required"));
            return;
        }
        if (!ids.Add(value)) issues.Add(new("duplicate-id", $"{path}.id", $"Duplicate ID '{value}'"));
    }

    private static void ValidatePrompt(PromptDto? prompt, string path, List<CatalogValidationIssue> issues)
    {
        if (prompt is null)
        {
            issues.Add(new("missing-prompt", path, "Prompt object is required"));
            return;
        }
        if (string.IsNullOrWhiteSpace(prompt.English)) issues.Add(new("missing-prompt", $"{path}.english", "English prompt is required"));
        if (string.IsNullOrWhiteSpace(prompt.Neutral)) issues.Add(new("missing-prompt", $"{path}.neutral", "Neutral prompt is required"));
        foreach (var (value, language) in new[]
                 {
                     (prompt.Neutral, "neutral"),
                     (prompt.Japanese, "japanese"),
                     (prompt.German, "german"),
                     (prompt.French, "french"),
                 })
            if (!string.IsNullOrWhiteSpace(value))
                foreach (var label in ForbiddenNonEnglishAccentLabels)
                    if (value.Contains(label, StringComparison.OrdinalIgnoreCase))
                        issues.Add(new("invalid-neutral-prompt", $"{path}.{language}", $"Non-English prompt contains English accent label '{label}'"));
    }

    private static void ValidateSlotField(string? value, string path, string field, List<CatalogValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value)) issues.Add(new("malformed-slot", $"{path}.{field}", "Slot field is required"));
    }

    private static void ValidateConfidence(string? confidence, string path, List<CatalogValidationIssue> issues)
    {
        if (confidence is not ("A" or "B" or "C" or "Ø"))
            issues.Add(new("invalid-confidence", path, "Confidence must be A, B, C, or Ø"));
    }

    private static void ValidateSlotReferences(
        List<string>? references,
        string sex,
        string path,
        IReadOnlyDictionary<string, SlotTemplateDto> slots,
        List<CatalogValidationIssue> issues)
    {
        var slotPath = $"{path}.slotTemplates.{sex}";
        if (references is null || references.Count != 5)
        {
            issues.Add(new("invalid-slot-count", slotPath, "Each domain requires exactly five slots"));
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in references)
        {
            if (id is null)
            {
                issues.Add(new("null-reference", slotPath, "Slot template reference cannot be null"));
                continue;
            }
            if (!slots.TryGetValue(id, out var slot))
                issues.Add(new("missing-reference", slotPath, $"Unknown slot template '{id}'"));
            else if (!String.Equals(slot.Sex, sex, StringComparison.Ordinal))
                issues.Add(new("malformed-slot", slotPath, $"Slot '{id}' is not a {sex} template"));
            if (!seen.Add(id)) issues.Add(new("duplicate-slot", slotPath, $"Slot '{id}' appears more than once"));
        }
    }

    private static void ValidateModifierReferences(
        List<string>? references,
        string path,
        HashSet<string> modifiers,
        List<CatalogValidationIssue> issues)
    {
        foreach (var id in references ?? [])
            if (id is null)
                issues.Add(new("null-reference", $"{path}.modifierIds", "Modifier reference cannot be null"));
            else if (!modifiers.Contains(id))
                issues.Add(new("missing-reference", $"{path}.modifierIds", $"Unknown modifier '{id}'"));
    }

    private static void ValidateInheritanceCycles(
        IReadOnlyList<DomainDto> domains,
        HashSet<string> domainIds,
        List<CatalogValidationIssue> issues)
    {
        for (var index = 0; index < domains.Count; index++)
        {
            var domain = domains[index];
            if (domain is null) continue;
            if (!string.IsNullOrWhiteSpace(domain.Inherits) && !domainIds.Contains(domain.Inherits))
                issues.Add(new("missing-reference", $"domains[{index}].inherits", $"Unknown domain '{domain.Inherits}'"));
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var domain in domains)
            if (domain is not null) Visit(domain, domains, state, issues);
    }

    private static void Visit(
        DomainDto domain,
        IReadOnlyList<DomainDto> domains,
        Dictionary<string, int> state,
        List<CatalogValidationIssue> issues)
    {
        if (domain.Id is null) return;
        if (state.TryGetValue(domain.Id, out var current))
        {
            if (current == 1) issues.Add(new("reference-cycle", $"domains[{domain.Id}].inherits", "Domain inheritance cycle detected"));
            return;
        }
        state[domain.Id] = 1;
        if (domain.Inherits is not null)
        {
            var parent = domains.FirstOrDefault(candidate => candidate is not null && candidate.Id == domain.Inherits);
            if (parent is not null) Visit(parent, domains, state, issues);
        }
        state[domain.Id] = 2;
    }

    private static CastingDomain ToDomain(DomainDto dto, IReadOnlyDictionary<string, CastingSlotTemplate> slots) => new(
        dto.Id!, dto.Confidence!, dto.FallbackDimensions!, dto.Prompts!.English!, dto.Prompts.Neutral!, dto.Inherits,
        dto.SlotTemplates!.Masculine!.Select(id => slots[id]).ToArray(),
        dto.SlotTemplates.Feminine!.Select(id => slots[id]).ToArray(),
        dto.Prompts.Japanese, dto.Prompts.German, dto.Prompts.French);

    private sealed class CatalogDocument
    {
        public int Version { get; set; }
        public string? DefaultDomain { get; set; }
        public List<SlotTemplateDto>? SlotTemplates { get; set; }
        public List<DomainDto>? Domains { get; set; }
        public List<ModifierDto>? Modifiers { get; set; }
        public List<IdentityGroupDto>? IdentityGroups { get; set; }
        public List<RuleDto>? Rules { get; set; }
        public List<MetadataRuleDto>? MetadataRules { get; set; }
        public List<TerritoryDto>? Territories { get; set; }
    }

    private sealed class PromptDto
    {
        public string? English { get; set; }
        public string? Neutral { get; set; }
        public string? Japanese { get; set; }
        public string? German { get; set; }
        public string? French { get; set; }
    }

    private sealed class SlotTemplateDto
    {
        public string? Id { get; set; }
        public string? Sex { get; set; }
        public string? Age { get; set; }
        public string? Physique { get; set; }
        public string? BodyType { get; set; }
        public string? HeightBucket { get; set; }
        public string? MuscleMassBucket { get; set; }
        public string? Register { get; set; }
        public string? Personality { get; set; }
        public PromptDto? Prompts { get; set; }
    }

    private sealed class DomainDto
    {
        public string? Id { get; set; }
        public string? Confidence { get; set; }
        public string? Inherits { get; set; }
        public string? FallbackDimensions { get; set; } = "sex_age";
        public PromptDto? Prompts { get; set; }
        public DomainSlotReferencesDto? SlotTemplates { get; set; }
    }

    private sealed class DomainSlotReferencesDto
    {
        public List<string>? Masculine { get; set; }
        public List<string>? Feminine { get; set; }
    }

    private sealed class ModifierDto
    {
        public string? Id { get; set; }
        public PromptDto? Prompts { get; set; }
    }

    private sealed class IdentityGroupDto
    {
        public string? Id { get; set; }
        public List<uint>? NpcBaseIds { get; set; }
        public string? DomainId { get; set; }
        public List<string>? ModifierIds { get; set; }
        public string? Confidence { get; set; }
    }

    private sealed class RuleDto
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Value { get; set; }
        public string? DomainId { get; set; }
        public List<string>? ModifierIds { get; set; }
        public List<string>? TerritoryPlaceNames { get; set; }
        public string? SpeakerCategory { get; set; }
        public int Priority { get; set; }
        public string? Confidence { get; set; }
    }

    private sealed class MetadataRuleDto
    {
        public string? Id { get; set; }
        public string? DomainId { get; set; }
        public List<string>? ModifierIds { get; set; }
        public List<string>? TerritoryPlaceNames { get; set; }
        public string? SpeakerCategory { get; set; }
        public int Priority { get; set; }
        public string? Confidence { get; set; }
        public string? Source { get; set; }
        public string? VoiceSex { get; set; }
        public MetadataMatchDto? Match { get; set; }
    }

    private sealed class MetadataMatchDto
    {
        public List<int>? RaceIds { get; set; }
        public List<int>? TribeIds { get; set; }
        public List<int>? BodyTypeIds { get; set; }
        public List<int>? HeightValues { get; set; }
        public List<long>? ModelCharaIds { get; set; }
        public List<int>? ModelFamilyIds { get; set; }
        public List<int>? ModelTypes { get; set; }
        public List<int>? ModelBases { get; set; }
        public List<int>? ModelVariants { get; set; }
        public List<long>? ModelHeadIds { get; set; }
        public List<long>? ModelBodyIds { get; set; }
        public List<long>? ModelHandsIds { get; set; }
        public List<long>? ModelLegsIds { get; set; }
        public List<long>? ModelFeetIds { get; set; }

        public bool HasConstraint =>
            RaceIds is { Count: > 0 } || TribeIds is { Count: > 0 } || BodyTypeIds is { Count: > 0 }
            || HeightValues is { Count: > 0 } || ModelCharaIds is { Count: > 0 }
            || ModelFamilyIds is { Count: > 0 } || ModelTypes is { Count: > 0 }
            || ModelBases is { Count: > 0 } || ModelVariants is { Count: > 0 }
            || ModelHeadIds is { Count: > 0 } || ModelBodyIds is { Count: > 0 }
            || ModelHandsIds is { Count: > 0 } || ModelLegsIds is { Count: > 0 }
            || ModelFeetIds is { Count: > 0 };
    }

    private sealed class TerritoryDto
    {
        public string? PlaceName { get; set; }
        public string? Confidence { get; set; }
        public List<PriorDto>? Priors { get; set; }
    }

    private sealed class PriorDto
    {
        public string? DomainId { get; set; }
        public double Weight { get; set; }
        public List<string>? AllowedSpeakerCategories { get; set; }
        public List<string>? ModifierIds { get; set; }
    }
}
