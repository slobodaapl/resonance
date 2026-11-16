namespace Resonance.Game;

public sealed record QuestDialogueLine(string QuestSheet, int Index, string Speaker, string Text);
public sealed record PrefetchUpdate(bool Synchronized, bool Resynchronized, IReadOnlyList<QuestDialogueLine> Future);

public sealed class QuestDialogueMatcher(IReadOnlyList<IReadOnlyList<QuestDialogueLine>> candidates)
{
    private sealed record Match(IReadOnlyList<QuestDialogueLine> Quest, int Index)
    {
        public QuestDialogueLine Line => Quest[Index];
    }

    private readonly Dictionary<string, Match[]> byText = candidates
        .SelectMany(quest => quest.Select((_, index) => new Match(quest, index)))
        .GroupBy(match => DialogueText.NormalizeText(match.Line.Text), StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
    private IReadOnlyList<QuestDialogueLine>? active;
    private int cursor = -1;
    private readonly Dictionary<string, HashSet<string>> actorTokens = new(StringComparer.Ordinal);

    public PrefetchUpdate Observe(string speaker, string text, int lookahead = Int32.MaxValue,
        string? actorKey = null)
    {
        var normalizedSpeaker = DialogueText.NormalizeSpeaker(speaker);
        var matches = byText.GetValueOrDefault(DialogueText.NormalizeText(text)) ?? [];
        var knownActors = actorKey is { } id ? actorTokens.GetValueOrDefault(id) : null;
        var actorMatches = matches.Where(match => actorKey is not null
            ? knownActors?.Contains(DialogueText.NormalizeSpeaker(match.Line.Speaker)) == true
            : normalizedSpeaker.Length != 0
                && DialogueText.NormalizeSpeaker(match.Line.Speaker) == normalizedSpeaker).ToArray();
        if (actorMatches.Length != 0) matches = actorMatches;
        else if (matches.Length != 0)
        {
            var actor = DialogueText.NormalizeSpeaker(matches[0].Line.Speaker);
            if (matches.Any(match => DialogueText.NormalizeSpeaker(match.Line.Speaker) != actor)
                || matches.Length > 1 && actor.Length == 0)
                matches = [];
        }
        var resynchronized = false;
        if (active is not null
            && matches.Any(match => ReferenceEquals(match.Quest, active) && match.Index == cursor + 1))
            cursor++;
        else
        {
            resynchronized = active is not null;
            active = null;
            cursor = -1;
            if (matches.Length != 1) return new(false, resynchronized, []);
            active = matches[0].Quest;
            cursor = matches[0].Index;
        }
        if (actorKey is { } observedId)
        {
            if (!actorTokens.TryGetValue(observedId, out var tokens))
                actorTokens[observedId] = tokens = new(StringComparer.Ordinal);
            tokens.Add(DialogueText.NormalizeSpeaker(active[cursor].Speaker));
        }
        return new(true, resynchronized, active.Skip(cursor + 1).Take(Math.Max(0, lookahead)).ToArray());
    }
}
