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
    public static string? FallbackSpeakerKey(string? cutscenePath, string actorToken, string displayName)
    {
        if (cutscenePath is null) return null;
        var parts = cutscenePath.Split('/');
        if (parts.Length < 3) return null;
        var actor = DialogueText.NormalizeSpeaker(actorToken);
        var name = DialogueText.NormalizeSpeaker(displayName);
        if (actor.Length == 0 || name.Length == 0 || actor.All(Char.IsDigit)
            || actor is "all" or "system" or "narration" or "narrator" or "memory" or "nonevoice")
            return null;
        return $"cutb:{parts[0]}:{parts[1]}:{actor}:{name}";
    }

    public string SourceKey { get; init; } = Key;
    public int? SourceOffset { get; init; }
    public string NodeId { get; init; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Key))).ToLowerInvariant();
}

public sealed record CutsceneVoiceEdge(string? CurrentNodeId, string? NextNodeId);

public sealed class CutsceneVoiceManifest(
    uint cutsceneId,
    string cutscenePath,
    IReadOnlyList<CutsceneVoiceLine> lines,
    IReadOnlyList<CutsceneVoiceEdge>? edges = null)
{
    private readonly IReadOnlyDictionary<string, CutsceneVoiceLine[]> successors =
        edges is null ? BuildSuccessors(lines) : BuildSuccessors(lines, edges);
    private readonly Dictionary<string, CutsceneVoiceLine[]> byText = lines
        .GroupBy(line => DialogueText.NormalizeText(line.Text), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    private readonly Dictionary<string, CutsceneVoiceLine[]> bySourceKey = lines
        .GroupBy(line => line.SourceKey, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    public uint CutsceneId { get; } = cutsceneId;
    public string CutscenePath { get; } = cutscenePath;
    public IReadOnlyList<CutsceneVoiceLine> Lines { get; } = lines;
    public IReadOnlyList<CutsceneVoiceEdge>? Edges { get; } = edges;

    public IReadOnlyList<CutsceneVoiceLine> StartNodes => Edges is null
        ? Lines.Count == 0
            ? []
            : Lines.Where(line => line.Ordinal == Lines.Min(value => value.Ordinal)).ToArray()
        : ResolveStarts(Lines, Edges);

    public IReadOnlyList<CutsceneVoiceLine> MatchFrontier(
        string speaker, string text, IReadOnlyCollection<string> frontierNodeIds,
        uint? npcBaseId = null, IReadOnlySet<string>? actorIdentities = null, string? rowKey = null,
        int? rowOffset = null)
    {
        if (rowKey is not null)
        {
            var sourceMatches = bySourceKey.GetValueOrDefault(rowKey) ?? [];
            if (rowOffset is not null && sourceMatches.Any(line => line.SourceOffset is not null))
                return sourceMatches.Where(line => line.SourceOffset == rowOffset).ToArray();
            var atFrontier = sourceMatches.Where(line => frontierNodeIds.Contains(line.NodeId)).ToArray();
            return atFrontier.Length != 0 ? atFrontier : sourceMatches;
        }
        var textMatches = byText.GetValueOrDefault(DialogueText.NormalizeText(text)) ?? [];
        if (npcBaseId is not null)
            textMatches = textMatches.Where(line => line.ActorNpcBaseId is null
                || line.ActorNpcBaseId == npcBaseId).ToArray();
        if (textMatches.Length == 0) return [];
        var normalizedSpeaker = DialogueText.NormalizeSpeaker(speaker);
        var speakerHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedSpeaker))).ToLowerInvariant();
        bool MatchesActor(CutsceneVoiceLine line) => normalizedSpeaker.Length != 0
            && (line.ActorTokenHash is not null
                ? String.Equals(line.ActorTokenHash, speakerHash, StringComparison.Ordinal)
                : DialogueText.NormalizeSpeaker(line.ActorToken) == normalizedSpeaker);
        var matches = npcBaseId is not null || actorIdentities is not null
            ? textMatches.Where(line => npcBaseId is not null && line.ActorNpcBaseId == npcBaseId
                || actorIdentities?.Contains(ActorIdentity(line)) == true).ToArray()
            : textMatches.Where(MatchesActor).ToArray();
        if (matches.Length == 0)
        {
            // The whole scene must agree; a frontier can hide conflicting actors or voice status.
            var first = textMatches[0];
            var actor = ActorIdentity(first);
            if (textMatches.Any(line => line.IsVoiced != first.IsVoiced
                    || line.IsPlayerChoice != first.IsPlayerChoice
                    || ActorIdentity(line) != actor)
                || textMatches.Length > 1 && actor.Length == 0)
                return [];
            matches = textMatches;
        }
        return frontierNodeIds.Count == 0
            ? matches
            : matches.Where(line => frontierNodeIds.Contains(line.NodeId)).ToArray();
    }

    internal static string ActorIdentity(CutsceneVoiceLine line) =>
        line.ActorTokenHash ?? DialogueText.NormalizeSpeaker(line.ActorToken);

    public bool HasOnlyNativeDialogue(string text, uint? npcBaseId = null)
    {
        var matches = byText.GetValueOrDefault(DialogueText.NormalizeText(text)) ?? [];
        if (npcBaseId is not null)
            matches = matches.Where(line => line.ActorNpcBaseId is null || line.ActorNpcBaseId == npcBaseId).ToArray();
        return matches.Length != 0 && matches.All(line => line.IsVoiced && !line.IsPlayerChoice);
    }

    public IReadOnlyList<CutsceneVoiceLine> Successors(IEnumerable<CutsceneVoiceLine> current) =>
        current.SelectMany(line => successors.TryGetValue(line.NodeId, out var next) ? next : [])
            .DistinctBy(line => line.NodeId).ToArray();

    public CutsceneVoiceLine? Match(string speaker, string text, int afterOrder = -1)
    {
        var normalizedText = DialogueText.NormalizeText(text);
        var future = Lines.Where(line => line.Order > afterOrder).ToArray();
        var matches = future.Where(line => DialogueText.NormalizeText(line.Text) == normalizedText).ToArray();
        if (matches.Length == 0 && afterOrder >= 0)
            matches = Lines.Where(line => DialogueText.NormalizeText(line.Text) == normalizedText).ToArray();
        if (matches.Length == 1) return matches[0];
        var normalizedSpeaker = DialogueText.NormalizeSpeaker(speaker);
        if (matches.Length > 1)
        {
            var actorMatches = matches.Where(line => DialogueText.NormalizeSpeaker(line.ActorToken) == normalizedSpeaker).ToArray();
            return actorMatches.Length == 0 ? null : actorMatches[0];
        }

        var talkCandidates = future.Where(line => !line.IsPlayerChoice).ToArray();
        if (talkCandidates.Length == 0) return null;
        var nextOrdinal = talkCandidates.Min(line => line.Ordinal);
        var next = talkCandidates.Where(line => line.Ordinal == nextOrdinal).ToArray();
        if (next.Length == 1) return next[0];
        var nextActorMatches = next
            .Where(line => DialogueText.NormalizeSpeaker(line.ActorToken) == normalizedSpeaker)
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

}

public static partial class CutsceneVoiceManifestParser
{
    public static IReadOnlyList<string> ExtractDialogueSheetNames(ReadOnlySpan<byte> cutb)
    {
        var names = CutsceneFlowParser.ReadDialogueSheetNames(cutb);
        if (names.Count != 0) return names;
        return DialogueSheetPattern().Matches(Encoding.UTF8.GetString(cutb))
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static CutsceneVoiceManifest Parse(
        uint cutsceneId,
        string cutscenePath,
        ReadOnlySpan<byte> cutb,
        Func<string, IReadOnlyDictionary<string, string>?> loadSheet,
        string languageCode,
        int? voiceLanguage = null)
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
        var flow = CutsceneFlowParser.Parse(cutb);
        if (flow is null) return new(cutsceneId, cutscenePath, lines);
        var byKey = lines.ToDictionary(line => line.SourceKey, StringComparer.OrdinalIgnoreCase);
        var voiceIndex = voiceLanguage ?? languageCode switch { "ja" => 0, "en" => 1, "de" => 2, "fr" => 3,
            _ => throw new NotSupportedException($"Unsupported cutscene voice language '{languageCode}'") };
        if (voiceIndex is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(voiceLanguage));
        var decoded = new List<CutsceneVoiceLine>();
        var byOffset = new Dictionary<int, CutsceneVoiceLine>();
        foreach (var cue in flow.Cues)
        {
            if (!byKey.TryGetValue(cue.Key, out var original))
            {
                TryParseQuestKey(cue.Key, out var actor, out var ordinal, out _);
                if (actor.Length == 0)
                {
                    var tokens = cue.Key.Split('_');
                    if (tokens.Length >= 5 && Int32.TryParse(tokens[^2], out ordinal)) actor = tokens[^1];
                }
                original = new(cue.Key, actor, textByKey.GetValueOrDefault(cue.Key) ?? String.Empty,
                    false, null, decoded.Count, ordinal);
            }
            var key = $"{cue.Key}@{cue.Offset:x}";
            var line = original with
            {
                Key = key,
                SourceKey = cue.Key,
                SourceOffset = cue.Offset,
                NodeId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant(),
                IsVoiced = !cue.IsPlayerChoice && (cue.VoiceLanguageMask & (1 << voiceIndex)) != 0,
                IsPlayerChoice = cue.IsPlayerChoice || original.IsPlayerChoice,
                Order = decoded.Count,
            };
            decoded.Add(line);
            byOffset.Add(cue.Offset, line);
        }
        var cueKeys = flow.Cues.Select(cue => cue.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        decoded.AddRange(lines.Where(line => !cueKeys.Contains(line.SourceKey)));
        var graph = flow.Edges.Select(edge => new CutsceneVoiceEdge(
            edge.CurrentOffset is { } current ? byOffset[current].NodeId : null,
            edge.NextOffset is { } next ? byOffset[next].NodeId : null)).ToArray();
        return new(cutsceneId, cutscenePath, decoded, graph);
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
