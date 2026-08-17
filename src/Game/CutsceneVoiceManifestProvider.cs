using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Resonance.Game;

public sealed class CutsceneVoiceManifestProvider(
    IDataManager dataManager,
    IClientState clientState,
    IPluginLog log,
    CutscenePlanStore? planStore = null)
{
    private readonly Dictionary<(uint CutsceneId, string Language), CutsceneVoiceManifest?> cache = [];
    private CutsceneVoiceManifest? activeManifest;
    private readonly HashSet<string> frontier = new(StringComparer.Ordinal);
    public string LastStatus { get; private set; } = "not-queried";

    public void Reset()
    {
        cache.Clear();
        activeManifest = null;
        frontier.Clear();
        LastStatus = "not-queried";
    }

    public bool? IsCurrentCutsceneUnskippable()
    {
        try
        {
            var cutsceneId = ReadCurrentCutsceneId();
            if (cutsceneId is null) return null;
            var workIndices = dataManager.GetExcelSheet<CutsceneWorkIndex>();
            return workIndices.TryGetRow(cutsceneId.Value, out var workIndex)
                ? workIndex.WorkIndex == 0
                : null;
        }
        catch (Exception error)
        {
            log.Warning(error, "Current cutscene skippability could not be read");
            return null;
        }
    }

    public CutsceneVoiceLine? Resolve(ActualTalkLine talk)
    {
        var cutsceneId = ReadCurrentCutsceneId();
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
            foreach (var node in manifest.StartNodes) frontier.Add(node.NodeId);
        }
        var matches = manifest.MatchFrontier(talk.Speaker, talk.Text, frontier);
        if (matches.Count == 0 && frontier.Count != 0)
            matches = manifest.MatchFrontier(talk.Speaker, talk.Text, []);
        CutsceneVoiceLine? line = null;
        if (matches.Count != 0 && AreVoiceEquivalent(matches))
        {
            line = matches[0];
            frontier.Clear();
            foreach (var next in manifest.Successors(matches)) frontier.Add(next.NodeId);
        }
        LastStatus = line is null
            ? matches.Count > 1
                ? $"ambiguous-dialogue:{cutsceneId.Value}"
                : $"dialogue-not-in-manifest:{cutsceneId.Value}"
            : $"matched:{line.Key}";
        return line;
    }

    public CutsceneVoiceManifest? GetManifest(uint cutsceneId)
    {
        var language = clientState.ClientLanguage.ToString().ToLowerInvariant();
        var key = (cutsceneId, language);
        if (cache.TryGetValue(key, out var manifest)) return manifest;
        manifest = Load(cutsceneId, language);
        cache[key] = manifest;
        return manifest;
    }

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
                                 && String.Equals(line.ActorToken, first.ActorToken,
                                     StringComparison.OrdinalIgnoreCase));
    }

    private CutsceneVoiceManifest? Load(uint cutsceneId, string language)
    {
        try
        {
            var cutscenes = dataManager.GetExcelSheet<Cutscene>(clientState.ClientLanguage);
            if (!cutscenes.TryGetRow(cutsceneId, out var cutscene)) return null;
            var cutscenePath = cutscene.Path.ExtractText();
            if (String.IsNullOrWhiteSpace(cutscenePath)) return null;
            var cutb = dataManager.GetFile($"cut/{cutscenePath}.cutb");
            if (cutb is null) return null;
            var packed = planStore?.TryLoad(
                cutsceneId, cutscenePath, language, cutb.Data, LoadVoiceSheet);
            if (packed is not null) return packed;
            return CutsceneVoiceManifestParser.Parse(
                cutsceneId,
                cutscenePath,
                cutb.Data,
                LoadVoiceSheet,
                LanguageCode(language));
        }
        catch (Exception error)
        {
            log.Warning(error, "Cutscene voice manifest could not be loaded for {CutsceneId}", cutsceneId);
            return null;
        }
    }

    private IReadOnlyDictionary<string, string>? LoadVoiceSheet(string sheetName)
    {
        try
        {
            var rows = dataManager.GetExcelSheet<RawRow>(clientState.ClientLanguage, sheetName);
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
