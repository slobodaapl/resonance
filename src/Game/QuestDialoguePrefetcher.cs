using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Resonance.Game;

public sealed record QuestDialogueLine(string QuestSheet, int Index, string Speaker, string Text);
public sealed record PrefetchUpdate(bool Synchronized, bool Resynchronized, IReadOnlyList<QuestDialogueLine> Future);

public sealed partial class QuestDialoguePrefetcher(IDataManager dataManager, IClientState clientState)
{
    private sealed record Candidate(string Sheet, IReadOnlyList<QuestDialogueLine> Lines);
    private List<Candidate> candidates = [];
    private readonly object gate = new();
    private Candidate? active;
    private int cursor = -1;

    public unsafe void BeginSession()
    {
        lock (gate)
        {
        candidates = [];
        active = null;
        cursor = -1;
        var manager = QuestManager.Instance();
        if (manager == null) return;
        var questSheet = dataManager.GetExcelSheet<Quest>(clientState.ClientLanguage);
        var seen = new HashSet<uint>();
        foreach (ref var work in manager->NormalQuests)
        {
            if (work.QuestId == 0 || !seen.Add(work.QuestId)) continue;
            if (TryLoadQuest(questSheet, work.QuestId, out var candidate)) candidates.Add(candidate);
        }
        foreach (ref var work in manager->DailyQuests)
        {
            if (work.QuestId == 0 || !seen.Add(work.QuestId)) continue;
            if (TryLoadQuest(questSheet, work.QuestId, out var candidate)) candidates.Add(candidate);
        }
        }
    }

    public PrefetchUpdate Observe(string speaker, string text, int lookahead = Int32.MaxValue)
    {
        lock (gate)
        {
        var normalizedSpeaker = Normalize(speaker);
        var normalizedText = Normalize(text);
        var resynchronized = false;

        if (active is not null && cursor + 1 < active.Lines.Count)
        {
            var expected = active.Lines[cursor + 1];
            if (Matches(expected, normalizedSpeaker, normalizedText)) cursor++;
            else
            {
                active = null;
                cursor = -1;
                resynchronized = true;
            }
        }

        if (active is null)
        {
            var matches = candidates
                .SelectMany(candidate => candidate.Lines.Select((line, index) => (candidate, line, index)))
                .Where(value => Matches(value.line, normalizedSpeaker, normalizedText))
                .Take(2)
                .ToArray();
            if (matches.Length != 1) return new(false, resynchronized, []);
            active = matches[0].candidate;
            cursor = matches[0].index;
        }

        var future = active.Lines.Skip(cursor + 1).Take(Math.Max(0, lookahead)).ToArray();
        return new(true, resynchronized, future);
        }
    }

    public void EndSession()
    {
        lock (gate)
        {
        candidates.Clear();
        active = null;
        cursor = -1;
        }
    }

    private bool TryLoadQuest(Lumina.Excel.ExcelSheet<Quest> questSheet, uint runtimeId, out Candidate candidate)
    {
        candidate = null!;
        var rowId = runtimeId < 0x10000 ? runtimeId + 0x10000 : runtimeId;
        if (!questSheet.TryGetRow(rowId, out var quest) && !questSheet.TryGetRow(runtimeId, out quest)) return false;
        var internalId = quest.Id.ExtractText().Trim('[', ']').Trim();
        if (internalId.Length < 5) return false;
        var sheetName = $"quest/{internalId.Substring(internalId.Length - 5, 3)}/{internalId}";

        Lumina.Excel.ExcelSheet<RawRow> sheet;
        try { sheet = dataManager.GetExcelSheet<RawRow>(clientState.ClientLanguage, sheetName); }
        catch { return false; }
        var lines = new List<QuestDialogueLine>();
        var index = 0;
        foreach (var row in sheet)
        {
            var key = row.ReadStringColumn(0).ExtractText();
            var text = Normalize(row.ReadStringColumn(1).ExtractText());
            if (key.Length == 0 || text.Length == 0 || key.Contains("_SEQ_", StringComparison.Ordinal)
                || key.Contains("_TODO_", StringComparison.Ordinal) || key.Contains("_SYSTEM_", StringComparison.Ordinal)) continue;
            var tokens = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) continue;
            var speakerIndex = tokens[3].All(char.IsDigit) ? 4 : 3;
            if (speakerIndex >= tokens.Length) continue;
            var rowSpeaker = Normalize(tokens[speakerIndex].Replace('＠', ' '));
            if (rowSpeaker.Length == 0) continue;
            lines.Add(new(sheetName, index, rowSpeaker, text));
            index++;
        }
        if (lines.Count == 0) return false;
        candidate = new(sheetName, lines);
        return true;
    }

    private static bool Matches(QuestDialogueLine line, string speaker, string text) =>
        Normalize(line.Speaker).Equals(speaker, StringComparison.OrdinalIgnoreCase)
        && Normalize(line.Text).Equals(text, StringComparison.Ordinal);

    private static string Normalize(string value) => Whitespace().Replace(value, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
