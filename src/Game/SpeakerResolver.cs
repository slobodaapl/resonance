using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using Resonance.Tts;

namespace Resonance.Game;

public sealed record ResolvedSpeaker(
    string StableKey,
    uint? NpcBaseId,
    string DisplayName,
    string Archetype,
    nint ActorAddress,
    bool SceneLocal,
    SpeakerCastingEvidence Evidence,
    SpeakerMetadata Metadata,
    string Sex);

/// <summary>
/// Resolves a talk speaker to a stable identity and live, typed appearance
/// evidence. It deliberately does not infer cultural origin from names or
/// playable race; only exact catalog values are emitted.
/// </summary>
public sealed class SpeakerResolver
{
    private readonly IObjectTable objects;
    private readonly IDataManager? dataManager;
    private readonly CastingProfileCatalog? catalog;
    private readonly Func<string?> currentTerritory;
    private readonly HashSet<string> nonHumanoidEvidenceValues;
    private readonly Dictionary<nint, bool> targetable = [];
    private IGameObject? nextUnknown;
    private long observedEpoch = long.MinValue;

    public SpeakerResolver(
        IObjectTable objects,
        IDataManager? dataManager = null,
        CastingProfileCatalog? catalog = null,
        Func<string?>? currentTerritory = null)
    {
        this.objects = objects;
        this.dataManager = dataManager;
        this.catalog = catalog;
        this.currentTerritory = currentTerritory ?? (() => null);
        nonHumanoidEvidenceValues = catalog?.Rules
            .Where(rule => String.Equals(rule.SpeakerCategory, "non_humanoid", StringComparison.Ordinal))
            .Select(rule => rule.Value)
            .ToHashSet(StringComparer.Ordinal)
            ?? [];
    }

    public ResolvedSpeaker Resolve(ActualTalkLine line, long sessionEpoch)
    {
        ObserveUnknownActorTransitions(sessionEpoch);
        if (line.Speaker == "???" && nextUnknown is { } unknown && unknown.IsValid() && unknown.BaseId != 0)
            return ResolveObject(unknown, line.Speaker);

        if (line.Speaker is not "???" and not "Narrator")
        {
            var candidate = objects
                .Where(value => value.IsValid()
                    && value.BaseId != 0
                    && value.ObjectKind is ObjectKind.EventNpc or ObjectKind.BattleNpc
                    && string.Equals(value.Name.TextValue, line.Speaker, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.CurrentDistance)
                .ThenBy(value => value.ObjectIndex)
                .FirstOrDefault();
            if (candidate is not null)
                return ResolveObject(candidate, line.Speaker);
        }

        if (line.Speaker == "Narrator")
        {
            var evidence = EmptyEvidence("narrator", sceneLocal: false);
            return new("narrator", null, line.Speaker, "neutral_narrator", 0, false,
                evidence, new SpeakerMetadata(EvidenceSource: "curated"), "masculine");
        }

        var stableKey = $"scene:{sessionEpoch}:{line.Speaker.ToLowerInvariant()}";
        var sceneEvidence = EmptyEvidence(stableKey, sceneLocal: true);
        return new(stableKey, null, line.Speaker, "neutral_adult", 0, true,
            sceneEvidence, new SpeakerMetadata(EvidenceSource: "scene-local"), "masculine");
    }

    private void ObserveUnknownActorTransitions(long sessionEpoch)
    {
        if (observedEpoch != sessionEpoch)
        {
            observedEpoch = sessionEpoch;
            targetable.Clear();
            nextUnknown = null;
        }

        var present = new HashSet<nint>();
        var transitioned = new List<IGameObject>();
        foreach (var value in objects)
        {
            if (!value.IsValid() || value is not ICharacter character || value.BaseId == 0
                || value.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.BattleNpc)) continue;
            present.Add(value.Address);
            var current = character.IsTargetable;
            if (targetable.TryGetValue(value.Address, out var previous) && !previous && current)
                transitioned.Add(value);
            targetable[value.Address] = current;
        }
        foreach (var address in targetable.Keys.Where(address => !present.Contains(address)).ToArray())
            targetable.Remove(address);
        if (transitioned.Count > 0)
            nextUnknown = transitioned.OrderBy(value => value.CurrentDistance).ThenBy(value => value.ObjectIndex).First();
    }

    private ResolvedSpeaker ResolveObject(IGameObject candidate, string displayedName)
    {
        var stableKey = $"npc:{candidate.BaseId}";
        var live = ReadLiveEvidence(candidate, stableKey);
        return new(
            stableKey,
            candidate.BaseId,
            displayedName,
            live.Sex == "feminine" ? "feminine_adult" : "masculine_adult",
            candidate.Address,
            false,
            live.Evidence,
            live.Metadata,
            live.Sex);
    }

    private (SpeakerCastingEvidence Evidence, SpeakerMetadata Metadata, string Sex) ReadLiveEvidence(
        IGameObject candidate,
        string stableKey)
    {
        var placeName = currentTerritory();
        if (candidate is not ICharacter character)
        {
            var evidence = new SpeakerCastingEvidence(stableKey, placeName, placeName, candidate.BaseId);
            return (evidence, new SpeakerMetadata(EvidenceSource: "live"), "masculine");
        }

        try
        {
            var customize = character.CustomizeData;
            var sex = customize.Sex == 1 ? "feminine" : "masculine";
            var race = EnglishRace(customize.Race);
            var tribe = EnglishTribe(customize.Tribe);
            var modelChara = TryReadModelChara(candidate);
            var category = race is not null && nonHumanoidEvidenceValues.Contains(race)
                || tribe is not null && nonHumanoidEvidenceValues.Contains(tribe)
                || modelChara is not null && nonHumanoidEvidenceValues.Contains(
                    modelChara.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ? "non_humanoid"
                : "humanoid";
            var heightBucket = Bucket(customize.Height, "short", "average", "tall");
            var muscleBucket = Bucket(customize.MuscleMass, "low", "average", "high");
            var physique = muscleBucket switch
            {
                "high" => "heavy",
                "low" => "light",
                _ => "average",
            };
            var bodyType = customize.BodyType switch
            {
                0 => "broad",
                4 => "slender",
                _ => "average",
            };
            var evidence = new SpeakerCastingEvidence(
                stableKey,
                placeName,
                placeName,
                candidate.BaseId,
                sex,
                category,
                Tribe: tribe,
                Species: category == "non_humanoid" ? race : null,
                Race: race,
                ModelChara: modelChara?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Physique: physique,
                BodyType: bodyType,
                HeightBucket: heightBucket,
                MuscleMassBucket: muscleBucket);
            var metadata = new SpeakerMetadata(
                Gender: customize.Sex,
                Race: customize.Race,
                Tribe: customize.Tribe,
                Body: customize.BodyType,
                Height: customize.Height,
                MuscleMass: customize.MuscleMass,
                ModelCharaId: modelChara,
                Sex: sex,
                BodyType: bodyType,
                Physique: physique,
                EvidenceSource: "live");
            return (evidence, metadata, sex);
        }
        catch
        {
            var evidence = new SpeakerCastingEvidence(stableKey, placeName, placeName, candidate.BaseId);
            return (evidence, new SpeakerMetadata(EvidenceSource: "live"), "masculine");
        }
    }

    private string? EnglishRace(byte raceId)
    {
        if (dataManager is null || raceId == 0) return null;
        try
        {
            var sheet = dataManager.GetExcelSheet<Race>(ClientLanguage.English);
            if (!sheet.TryGetRow(raceId, out var row)) return null;
            var name = row.Masculine.ToString();
            return String.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    private string? EnglishTribe(byte tribeId)
    {
        if (dataManager is null || tribeId == 0) return null;
        try
        {
            var sheet = dataManager.GetExcelSheet<Tribe>(ClientLanguage.English);
            if (!sheet.TryGetRow(tribeId, out var row)) return null;
            var name = row.Masculine.ExtractText();
            return String.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch { return null; }
    }

    private static unsafe long? TryReadModelChara(IGameObject candidate)
    {
        if (candidate.Address == nint.Zero || !candidate.IsValid()) return null;
        try
        {
            var character = (Character*)candidate.Address;
            var id = character->ModelContainer.ModelCharaId;
            if (id <= 0) id = character->ModelContainer.ModelCharaId_2;
            return id > 0 ? id : null;
        }
        catch (NullReferenceException) { return null; }
        catch (AccessViolationException) { return null; }
    }

    private SpeakerCastingEvidence EmptyEvidence(string stableKey, bool sceneLocal)
    {
        var placeName = currentTerritory();
        return new SpeakerCastingEvidence(
            stableKey,
            placeName,
            placeName,
            Category: null,
            ModifierIds: sceneLocal ? ["scene_local"] : null);
    }

    private static string Bucket(byte value, string low, string middle, string high) => value switch
    {
        < 34 => low,
        > 66 => high,
        _ => middle,
    };
}
