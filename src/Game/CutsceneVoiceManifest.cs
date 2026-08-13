using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Resonance.Game;

public sealed record CutsceneVoiceLine(
    string Key,
    string ActorToken,
    string Text,
    bool IsVoiced,
    string? ScdPath,
    int Order,
    int Ordinal,
    bool IsPlayerChoice = false,
    string? OfficialGroupId = null,
    string? ActorTokenHash = null,
    uint? ActorNpcBaseId = null)
{
    public string NodeId { get; init; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant();
}

public sealed record CutsceneVoiceEdge(string? CurrentNodeId, string? NextNodeId);

public sealed partial class CutsceneVoiceManifest(
    uint cutsceneId,
    string cutscenePath,
    IReadOnlyList<CutsceneVoiceLine> lines,
    IReadOnlyList<CutsceneVoiceEdge>? edges = null)
{
    private readonly IReadOnlyDictionary<string, CutsceneVoiceLine[]> successors =
        edges is null ? BuildSuccessors(lines) : BuildSuccessors(lines, edges);
    public uint CutsceneId { get; } = cutsceneId;
    public string CutscenePath { get; } = cutscenePath;
    public IReadOnlyList<CutsceneVoiceLine> Lines { get; } = lines;

    public IReadOnlyList<CutsceneVoiceLine> StartNodes => edges is null
        ? Lines.Count == 0
            ? []
            : Lines.Where(line => line.Ordinal == Lines.Min(value => value.Ordinal)).ToArray()
        : ResolveStarts(Lines, edges);

    public IReadOnlyList<CutsceneVoiceLine> MatchFrontier(
        string speaker, string text, IReadOnlyCollection<string> frontierNodeIds)
    {
        var candidates = frontierNodeIds.Count == 0
            ? Lines
            : Lines.Where(line => frontierNodeIds.Contains(line.NodeId));
        var normalizedText = NormalizeText(text);
        var textMatches = candidates.Where(line => NormalizeText(line.Text) == normalizedText).ToArray();
        if (textMatches.Length == 0) return [];
        var normalizedSpeaker = NormalizeActor(speaker);
        var speakerHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSpeaker))).ToLowerInvariant();
        var actorMatches = textMatches.Where(line => line.ActorTokenHash is not null
                ? String.Equals(line.ActorTokenHash, speakerHash, StringComparison.Ordinal)
                : NormalizeActor(line.ActorToken) == normalizedSpeaker).ToArray();
        // Legacy NONE_VOICE rows lack actor attribution. Preserve exact-text
        // matching until a schema-v2 graph supplies the deterministic actor.
        if (actorMatches.Length == 0 && textMatches.Length == 1
            && textMatches[0].ActorToken == "NONE_VOICE") return textMatches;
        return actorMatches;
    }

    public IReadOnlyList<CutsceneVoiceLine> Successors(IEnumerable<CutsceneVoiceLine> current) =>
        current.SelectMany(line => successors.TryGetValue(line.NodeId, out var next) ? next : [])
            .DistinctBy(line => line.NodeId).ToArray();

    public CutsceneVoiceLine? Match(string speaker, string text, int afterOrder = -1)
    {
        var normalizedText = NormalizeText(text);
        var future = Lines.Where(line => line.Order > afterOrder).ToArray();
        var matches = future.Where(line => NormalizeText(line.Text) == normalizedText).ToArray();
        if (matches.Length == 0 && afterOrder >= 0)
            matches = Lines.Where(line => NormalizeText(line.Text) == normalizedText).ToArray();
        if (matches.Length == 1) return matches[0];
        var normalizedSpeaker = NormalizeActor(speaker);
        if (matches.Length > 1)
        {
            var actorMatches = matches.Where(line => NormalizeActor(line.ActorToken) == normalizedSpeaker).ToArray();
            return actorMatches.Length == 0 ? null : actorMatches[0];
        }

        var talkCandidates = future.Where(line => !line.IsPlayerChoice).ToArray();
        if (talkCandidates.Length == 0) return null;
        var nextOrdinal = talkCandidates.Min(line => line.Ordinal);
        var next = talkCandidates.Where(line => line.Ordinal == nextOrdinal).ToArray();
        if (next.Length == 1) return next[0];
        var nextActorMatches = next
            .Where(line => NormalizeActor(line.ActorToken) == normalizedSpeaker)
            .ToArray();
        return nextActorMatches.Length == 1 ? nextActorMatches[0] : null;
    }

    public IReadOnlyList<CutsceneVoiceLine> SyntheticFuture(CutsceneVoiceLine current) =>
        ReachableFrom(current)
            .Where(line => !line.IsVoiced && !line.IsPlayerChoice)
            .ToArray();

    public CutsceneVoiceLine? ImmediateSuccessor(CutsceneVoiceLine current) =>
        successors.TryGetValue(current.NodeId, out var next) && next.Length == 1 ? next[0] : null;

    private IReadOnlyList<CutsceneVoiceLine> ReachableFrom(CutsceneVoiceLine current)
    {
        var result = new List<CutsceneVoiceLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { current.NodeId };
        var queue = new Queue<CutsceneVoiceLine>();
        if (successors.TryGetValue(current.NodeId, out var immediate))
            foreach (var line in immediate) queue.Enqueue(line);
        while (queue.TryDequeue(out var line))
        {
            if (!seen.Add(line.NodeId)) continue;
            result.Add(line);
            if (line.IsPlayerChoice) continue;
            if (successors.TryGetValue(line.NodeId, out var next))
                foreach (var candidate in next) queue.Enqueue(candidate);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, CutsceneVoiceLine[]> BuildSuccessors(
        IReadOnlyList<CutsceneVoiceLine> lines)
    {
        var groups = lines.GroupBy(line => line.Ordinal).OrderBy(group => group.Key)
            .Select(group => group.ToArray()).ToArray();
        var result = new Dictionary<string, CutsceneVoiceLine[]>(StringComparer.Ordinal);
        for (var index = 0; index < groups.Length; index++)
        {
            var next = index + 1 < groups.Length ? groups[index + 1] : [];
            foreach (var line in groups[index]) result[line.NodeId] = next;
        }
        return result;
    }


    private static IReadOnlyDictionary<string, CutsceneVoiceLine[]> BuildSuccessors(
        IReadOnlyList<CutsceneVoiceLine> lines, IReadOnlyList<CutsceneVoiceEdge> edges)
    {
        var byId = lines.ToDictionary(line => line.NodeId, StringComparer.Ordinal);
        var result = lines.ToDictionary(line => line.NodeId, _ => Array.Empty<CutsceneVoiceLine>(),
            StringComparer.Ordinal);
        foreach (var group in edges.Where(edge => edge.CurrentNodeId is not null
                                                   && edge.NextNodeId is not null)
                     .GroupBy(edge => edge.CurrentNodeId!, StringComparer.Ordinal))
            result[group.Key] = group.Select(edge => byId[edge.NextNodeId!]).DistinctBy(line => line.NodeId).ToArray();
        return result;
    }

    private static IReadOnlyList<CutsceneVoiceLine> ResolveStarts(
        IReadOnlyList<CutsceneVoiceLine> lines, IReadOnlyList<CutsceneVoiceEdge> edges)
    {
        var byId = lines.ToDictionary(line => line.NodeId, StringComparer.Ordinal);
        return edges.Where(edge => edge.CurrentNodeId is null && edge.NextNodeId is not null)
            .Select(edge => byId[edge.NextNodeId!]).DistinctBy(line => line.NodeId).ToArray();
    }

    private static string NormalizeText(string value)
    {
        value = SpeakerLabelPattern().Replace(value, String.Empty);
        return String.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeActor(string value) => new(value
        .Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant)
        .ToArray());

    [GeneratedRegex(@"^(?:\(-[^)]*-\))+\s*")]
    private static partial Regex SpeakerLabelPattern();
}

public static partial class CutsceneVoiceManifestParser
{
    public static IReadOnlyList<string> ExtractDialogueSheetNames(ReadOnlySpan<byte> cutb) =>
        DialogueSheetPattern().Matches(Encoding.UTF8.GetString(cutb))
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static CutsceneVoiceManifest Parse(
        uint cutsceneId,
        string cutscenePath,
        ReadOnlySpan<byte> cutb,
        Func<string, IReadOnlyDictionary<string, string>?> loadSheet,
        string languageCode)
    {
        var raw = Encoding.UTF8.GetString(cutb);
        var sheetNames = ExtractDialogueSheetNames(cutb);
        var textByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sheetName in sheetNames)
        {
            var sheet = loadSheet(sheetName);
            if (sheet is null) continue;
            foreach (var (key, text) in sheet) textByKey.TryAdd(key, text);
        }

        var parsed = new List<(string Key, string Actor, string Text, bool Voiced,
            string? ScdPath, int Ordinal, bool PlayerChoice, int RawOrder)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DialogueKeyPattern().Matches(raw))
        {
            var key = match.Value;
            if (!seen.Add(key) || !textByKey.TryGetValue(key, out var text)) continue;
            var voiceMatch = VoiceKeyPattern().Match(key);
            if (voiceMatch.Success)
            {
                var group = voiceMatch.Groups["group"].Value;
                var tail = voiceMatch.Groups["tail"].Value;
                var noneVoice = tail.EndsWith("_NONE_VOICE", StringComparison.OrdinalIgnoreCase);
                string actor;
                string lineId;
                if (noneVoice)
                {
                    actor = "NONE_VOICE";
                    lineId = tail[..^"_NONE_VOICE".Length];
                }
                else
                {
                    var separator = tail.LastIndexOf('_');
                    if (separator <= 0 || separator == tail.Length - 1) continue;
                    lineId = tail[..separator];
                    actor = tail[(separator + 1)..];
                }

                var ordinal = ParseLastNumber(lineId, parsed.Count);
                var scdPath = noneVoice ? null : BuildScdPath(
                    cutscenePath, group, lineId, languageCode);
                parsed.Add((key, actor, text, !noneVoice, scdPath, ordinal, noneVoice, parsed.Count));
            }
            else if (TryParseQuestKey(key, out var actor, out var ordinal, out var playerChoice))
            {
                parsed.Add((key, actor, text, false, null, ordinal, playerChoice, parsed.Count));
            }
        }
        var lines = parsed
            .OrderBy(value => value.Ordinal)
            .ThenBy(value => value.RawOrder)
            .Select((value, order) => new CutsceneVoiceLine(
                value.Key, value.Actor, value.Text, value.Voiced, value.ScdPath,
                order, value.Ordinal, value.PlayerChoice))
            .ToArray();
        return new(cutsceneId, cutscenePath, lines);
    }

    private static bool TryParseQuestKey(
        string key, out string actor, out int ordinal, out bool playerChoice)
    {
        actor = String.Empty;
        ordinal = 0;
        playerChoice = false;
        var tokens = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 6 || !tokens[0].Equals("TEXT", StringComparison.OrdinalIgnoreCase)
            || !Int32.TryParse(tokens[^1], out ordinal)) return false;
        var speakerIndex = tokens[3].All(char.IsDigit) ? 4 : 3;
        if (speakerIndex >= tokens.Length - 2) return false;
        actor = tokens[speakerIndex];
        playerChoice = PlayerChoiceActorPattern().IsMatch(actor);
        return actor.Length != 0;
    }

    private static int ParseLastNumber(string value, int fallback)
    {
        foreach (var token in value.Split('_', StringSplitOptions.RemoveEmptyEntries).Reverse())
            if (Int32.TryParse(token, out var parsed)) return parsed;
        return Int32.MaxValue / 2 + fallback;
    }

    private static string? BuildScdPath(
        string cutscenePath,
        string group,
        string lineId,
        string languageCode)
    {
        if (lineId.Length != 6 || !lineId.All(char.IsDigit)) return null;
        var expansion = cutscenePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (expansion is null || expansion.Equals("ffxiv", StringComparison.OrdinalIgnoreCase)) return null;
        if (!ExpansionPattern().IsMatch(expansion)) return null;
        var normalizedExpansion = expansion.ToLowerInvariant();
        var normalizedLanguage = languageCode.Trim().ToLowerInvariant();
        return $"cut/{normalizedExpansion}/sound/voicem/voiceman_{group}/"
               + $"vo_voiceman_{group}_{lineId}_m_{normalizedLanguage}.scd";
    }

    [GeneratedRegex(@"(?:cut_scene/[0-9]{3}/VoiceMan_[0-9]{5}|quest/[0-9]{3}/[A-Z0-9_]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex DialogueSheetPattern();

    [GeneratedRegex(@"TEXT_[A-Z0-9_]+(?=\0|[^A-Z0-9_]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex DialogueKeyPattern();

    [GeneratedRegex(@"^TEXT_VOICEMAN_(?<group>[0-9]{5})_(?<tail>[A-Z0-9_]+)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex VoiceKeyPattern();

    [GeneratedRegex(@"^ex[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex ExpansionPattern();

    [GeneratedRegex(@"^[QA][0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerChoiceActorPattern();

}
