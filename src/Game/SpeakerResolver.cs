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
    private readonly Func<string?> currentTerritory;
    private readonly Dictionary<nint, bool> targetable = [];
    private IGameObject? nextUnknown;
    private long observedEpoch = long.MinValue;

    public SpeakerResolver(
        IObjectTable objects,
        IDataManager? dataManager = null,
        Func<string?>? currentTerritory = null)
    {
        this.objects = objects;
        this.dataManager = dataManager;
        this.currentTerritory = currentTerritory ?? (() => null);
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

    public ResolvedSpeaker? ResolveNpcBase(uint npcBaseId, string displayedName)
    {
        if (npcBaseId == 0) return null;
        var candidate = objects
            .Where(value => value.IsValid() && value.BaseId == npcBaseId
                && value.ObjectKind is ObjectKind.EventNpc or ObjectKind.BattleNpc)
            .OrderBy(value => value.CurrentDistance)
            .ThenBy(value => value.ObjectIndex)
            .FirstOrDefault();
        return candidate is null ? null : ResolveObject(candidate, displayedName);
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
            var staticNpc = candidate.ObjectKind == ObjectKind.EventNpc
                ? TryReadNpcBase(candidate.BaseId)
                : null;
            var modelChara = TryReadModelChara(candidate) ??
                (staticNpc is { ModelChara.RowId: > 0 } npc ? npc.ModelChara.RowId : null);
            var model = TryReadModelCharaRow(modelChara);
            var category = model is { Type: 2 or 3 }
                && (staticNpc?.Race.RowId ?? customize.Race) == 0
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
                Age: CastingProfileCatalog.InferVoiceAge(customize.BodyType, customize.Height),
                Physique: physique,
                BodyType: bodyType,
                HeightBucket: heightBucket,
                MuscleMassBucket: muscleBucket,
                RaceId: customize.Race,
                TribeId: customize.Tribe,
                BodyTypeId: customize.BodyType,
                HeightValue: customize.Height,
                ModelCharaId: modelChara,
                ModelFamilyId: model?.Model,
                ModelType: model?.Type,
                ModelBase: model?.Base,
                ModelVariant: model?.Variant,
                ModelHeadId: staticNpc?.ModelHead,
                ModelBodyId: staticNpc?.ModelBody,
                ModelHandsId: staticNpc?.ModelHands,
                ModelLegsId: staticNpc?.ModelLegs,
                ModelFeetId: staticNpc?.ModelFeet);
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
                Age: CastingProfileCatalog.InferVoiceAge(customize.BodyType, customize.Height),
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

    private ENpcBase? TryReadNpcBase(uint baseId)
    {
        if (dataManager is null || baseId == 0) return null;
        try
        {
            var sheet = dataManager.GetExcelSheet<ENpcBase>();
            return sheet.TryGetRow(baseId, out var row) ? row : null;
        }
        catch { return null; }
    }

    private ModelChara? TryReadModelCharaRow(long? rowId)
    {
        if (dataManager is null || rowId is null or <= 0 || rowId > uint.MaxValue) return null;
        try
        {
            var sheet = dataManager.GetExcelSheet<ModelChara>();
            return sheet.TryGetRow((uint)rowId.Value, out var row) ? row : null;
        }
        catch { return null; }
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
