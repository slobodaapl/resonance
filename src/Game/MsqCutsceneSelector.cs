namespace Resonance.Game;

internal sealed record MsqQuestNode(
    uint QuestId,
    IReadOnlyList<uint> PreviousQuestIds,
    IReadOnlyList<uint> CutsceneIds);

internal static class MsqCutsceneSelector
{
    public static bool IsCutsceneParameter(string instruction) =>
        instruction.StartsWith("CUT", StringComparison.OrdinalIgnoreCase)
        || instruction.StartsWith("NCUT", StringComparison.OrdinalIgnoreCase)
        || instruction.StartsWith("LOC_CUT", StringComparison.OrdinalIgnoreCase)
        || instruction.StartsWith("LOC_NCUT", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<uint> SelectUpcoming(
        IReadOnlyList<MsqQuestNode> quests,
        uint frontierQuestId,
        bool frontierCompleted,
        Func<uint, bool> hasSeenCutscene,
        int limit)
    {
        if (limit <= 0) return [];
        var frontier = quests.Where(quest => quest.QuestId == frontierQuestId).ToArray();
        if (frontier.Length != 1) return [];
        var current = frontier[0];
        if (frontierCompleted)
        {
            var successors = Successors(quests, current, new HashSet<uint>());
            if (successors.Length != 1) return [];
            current = successors[0];
        }
        return SelectFrom(quests, current, hasSeenCutscene, limit);
    }

    private static IReadOnlyList<uint> SelectFrom(
        IReadOnlyList<MsqQuestNode> quests,
        MsqQuestNode current,
        Func<uint, bool> hasSeenCutscene,
        int limit)
    {
        var result = new List<uint>(limit);
        var seenScenes = new HashSet<uint>();
        var visitedQuests = new HashSet<uint>();
        while (visitedQuests.Add(current.QuestId))
        {
            foreach (var cutsceneId in current.CutsceneIds)
            {
                if (!seenScenes.Add(cutsceneId) || hasSeenCutscene(cutsceneId)) continue;
                result.Add(cutsceneId);
                if (result.Count == limit) return result;
            }

            var successors = Successors(quests, current, visitedQuests);
            if (successors.Length != 1) break;
            current = successors[0];
        }
        return result;
    }

    private static MsqQuestNode[] Successors(
        IReadOnlyList<MsqQuestNode> quests,
        MsqQuestNode current,
        IReadOnlySet<uint> visitedQuests) => quests.Where(quest =>
            !visitedQuests.Contains(quest.QuestId)
            && quest.PreviousQuestIds.Contains(current.QuestId))
        .ToArray();
}
