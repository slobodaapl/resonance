using Dalamud.Plugin.Services;
using Dalamud.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Resonance.Game;

public sealed class CutsceneVoiceManifestProvider
{
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly CutscenePlanStore? planStore;
    private readonly Func<int>? voiceLanguage;
    private readonly CutsceneManifestCache cache;
    private CutsceneVoiceManifest? activeManifest;
    private readonly HashSet<string> frontier = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> actorIdentities = new(StringComparer.Ordinal);
    public string LastStatus { get; private set; } = "not-queried";
    public bool CurrentDialogueIsNative { get; private set; }
    public uint? CurrentCutsceneId { get; private set; }

    public CutsceneVoiceManifestProvider(IDataManager dataManager, IClientState clientState, IPluginLog log,
        CutscenePlanStore? planStore = null, Func<int>? voiceLanguage = null)
    {
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.log = log;
        this.planStore = planStore;
        this.voiceLanguage = voiceLanguage;
        cache = new(Load);
    }

    internal void InvalidateCache() => cache.Clear();

    public void Reset()
    {
        activeManifest = null;
        frontier.Clear();
        actorIdentities.Clear();
        LastStatus = "not-queried";
        CurrentDialogueIsNative = false;
        CurrentCutsceneId = null;
    }

    public CutsceneVoiceLine? Resolve(ActualTalkLine talk)
    {
        CurrentDialogueIsNative = talk.Row is not null && talk.NativeVoice is not null;
        var cutsceneId = talk.Row is { CutsceneId: > 0 } source ? source.CutsceneId : ReadCurrentCutsceneId();
        CurrentCutsceneId = cutsceneId;
        if (cutsceneId is null)
        {
            LastStatus = "no-active-play-cutscene-task";
            return null;
        }
        var manifest = GetManifest(cutsceneId.Value);
        if (manifest is null)
        {
            LastStatus = $"manifest-unavailable:{cutsceneId.Value}";
            return null;
        }
        if (!ReferenceEquals(activeManifest, manifest))
        {
            activeManifest = manifest;
            frontier.Clear();
            actorIdentities.Clear();
            foreach (var node in manifest.StartNodes) frontier.Add(node.NodeId);
        }
        var npcBaseId = talk.Actor?.NpcBaseId;
        var knownActors = talk.Actor is { } actor ? actorIdentities.GetValueOrDefault(actor.StableKey) : null;
        var speaker = talk.Actor is null ? talk.Speaker : String.Empty;
        var matches = manifest.MatchFrontier(speaker, talk.Text, frontier, npcBaseId, knownActors,
            talk.RowKey, talk.Row?.Offset);
        if (matches.Count == 0 && frontier.Count != 0)
            matches = manifest.MatchFrontier(speaker, talk.Text, [], npcBaseId, knownActors,
                talk.RowKey, talk.Row?.Offset);
        CutsceneVoiceLine? line = null;
        if (matches.Count != 0 && AreVoiceEquivalent(matches))
        {
            line = matches[0];
            if (talk.Actor is { } observed)
            {
                if (!actorIdentities.TryGetValue(observed.StableKey, out var identities))
                    actorIdentities[observed.StableKey] = identities = new(StringComparer.Ordinal);
                foreach (var match in matches) identities.Add(CutsceneVoiceManifest.ActorIdentity(match));
            }
            frontier.Clear();
            foreach (var next in manifest.Successors(matches)) frontier.Add(next.NodeId);
        }
        CurrentDialogueIsNative = talk.NativeVoice is not null || (talk.Row is not null
            ? line?.IsVoiced == true
            : line?.IsVoiced ?? manifest.HasOnlyNativeDialogue(talk.Text, npcBaseId));
        LastStatus = line is not null ? $"matched:{line.Key}"
            : CurrentDialogueIsNative ? $"native-dialogue-with-ambiguous-speaker:{cutsceneId.Value}"
            : matches.Count > 1 ? $"ambiguous-dialogue:{cutsceneId.Value}"
            : $"dialogue-not-in-manifest:{cutsceneId.Value}";
        return line;
    }

    internal CutsceneManifestKey SnapshotKey(uint cutsceneId = 0)
    {
        var language = clientState.ClientLanguage;
        return new(cutsceneId, (int)language, voiceLanguage?.Invoke() ?? (int)language);
    }

    public CutsceneVoiceManifest? GetManifest(uint cutsceneId) => GetManifest(SnapshotKey(cutsceneId));

    internal CutsceneVoiceManifest? GetManifest(CutsceneManifestKey key) => cache.Get(key);

    public IReadOnlyList<CutsceneVoiceLine> GetSyntheticFuture(CutsceneVoiceLine current) =>
        activeManifest is null
            ? []
            : activeManifest.SyntheticFuture(current);

    public CutsceneVoiceLine? GetImmediateSuccessor(CutsceneVoiceLine current) =>
        activeManifest?.ImmediateSuccessor(current);

    public IReadOnlyList<CutsceneVoiceLine> GetImmediateSuccessors(CutsceneVoiceLine current) =>
        activeManifest?.Successors([current]) ?? [];

    private static bool AreVoiceEquivalent(IReadOnlyList<CutsceneVoiceLine> lines)
    {
        var first = lines[0];
        return lines.All(line => line.IsVoiced == first.IsVoiced
                                 && line.IsPlayerChoice == first.IsPlayerChoice
                                 && CutsceneVoiceManifest.ActorIdentity(line)
                                     == CutsceneVoiceManifest.ActorIdentity(first));
    }

    private CutsceneVoiceManifest? Load(CutsceneManifestKey key)
    {
        var cutsceneId = key.CutsceneId;
        var textLanguage = (ClientLanguage)key.TextLanguage;
        var language = textLanguage.ToString().ToLowerInvariant();
        try
        {
            var cutscenes = dataManager.GetExcelSheet<Cutscene>(textLanguage);
            if (!cutscenes.TryGetRow(cutsceneId, out var cutscene)) return null;
            var cutscenePath = cutscene.Path.ExtractText();
            if (String.IsNullOrWhiteSpace(cutscenePath)) return null;
            var cutb = dataManager.GetFile($"cut/{cutscenePath}.cutb");
            if (cutb is null) return null;
            var local = CutsceneVoiceManifestParser.Parse(
                cutsceneId,
                cutscenePath,
                cutb.Data,
                name => LoadVoiceSheet(name, textLanguage),
                LanguageCode(language), key.VoiceLanguage);
            CutsceneVoiceManifest? packed = null;
            try { packed = planStore?.TryLoad(cutsceneId, cutscenePath, language, cutb.Data,
                name => LoadVoiceSheet(name, textLanguage)); }
            catch (Exception error)
            {
                log.Warning(error, "Packed cutscene metadata rejected for {CutsceneId}; using local game data", cutsceneId);
            }
            if (packed is null) return local;
            var metadata = packed.Lines.GroupBy(line => line.SourceKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var lines = local.Lines.Select(line => metadata.TryGetValue(line.SourceKey, out var source)
                ? line with { OfficialGroupId = source.OfficialGroupId, ActorTokenHash = source.ActorTokenHash,
                    ActorNpcBaseId = source.ActorNpcBaseId }
                : line).ToArray();
            return new(cutsceneId, cutscenePath, lines, local.Edges);
        }
        catch (Exception error)
        {
            log.Warning(error, "Cutscene voice manifest could not be loaded for {CutsceneId}", cutsceneId);
            return null;
        }
    }

    private IReadOnlyDictionary<string, string>? LoadVoiceSheet(string sheetName, ClientLanguage language)
    {
        try
        {
            var rows = dataManager.GetExcelSheet<RawRow>(language, sheetName);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var key = row.ReadStringColumn(0).ExtractText();
                var text = row.ReadStringColumn(1).ExtractText();
                if (key.Length != 0 && text.Length != 0) result.TryAdd(key, text);
            }
            return result;
        }
        catch { return null; }
    }

    private static unsafe uint? ReadCurrentCutsceneId()
    {
        var framework = EventFramework.Instance();
        if (framework == null) return null;
        var tasks = framework->EventSceneModule.TaskManager.Tasks;
        for (var index = tasks.Count - 1; index >= 0; index--)
        {
            var task = tasks[index].Value;
            if (task == null || task->Type != EventSceneTaskType.PlayCutScene) continue;
            var cutscene = (PlayCutSceneTask*)task;
            if (cutscene->CutsceneId != 0) return cutscene->CutsceneId;
        }
        return null;
    }

    private static string LanguageCode(string language) => language switch
    {
        "english" => "en",
        "japanese" => "ja",
        "german" => "de",
        "french" => "fr",
        _ => throw new NotSupportedException($"Unsupported cutscene voice language '{language}'"),
    };
}
