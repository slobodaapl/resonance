using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Resonance.Game;

public sealed class MsqProgressReader(IDataManager dataManager, IClientState clientState)
{
    public unsafe IReadOnlyList<uint> GetUpcomingTrackedCutscenes(int limit)
    {
        if (limit <= 0) return [];
        var manager = QuestManager.Instance();
        var uiState = UIState.Instance();
        var scenarioTree = AgentScenarioTree.Instance();
        if (manager == null || uiState == null || scenarioTree == null || scenarioTree->Data == null) return [];

        var scenario = scenarioTree->Data;
        var pathQuestId = scenario->MSQPathIndex < 3
            ? scenario->MainScenarioQuestIds[scenario->MSQPathIndex]
            : (ushort)0;
        var frontierCompleted = pathQuestId == 0 || !manager->IsQuestAccepted(pathQuestId);
        var frontierRuntimeId = frontierCompleted
            ? scenario->MainScenarioQuestIds[3]
            : pathQuestId;
        if (frontierRuntimeId == 0) return [];
        var frontierQuestId = (uint)frontierRuntimeId + 0x10000;

        var language = clientState.ClientLanguage;
        var questSheet = dataManager.GetExcelSheet<Quest>(language);
        var cutsceneSheet = dataManager.GetExcelSheet<Cutscene>(language);
        var workIndexSheet = dataManager.GetExcelSheet<CutsceneWorkIndex>(language);
        var quests = new List<MsqQuestNode>();
        foreach (var quest in questSheet)
        {
            if (quest.EventIconType.RowId != 3) continue;
            var cutscenes = quest.QuestParams
                .Where(parameter => MsqCutsceneSelector.IsCutsceneParameter(
                    parameter.ScriptInstruction.ExtractText()))
                .Select(parameter => parameter.ScriptArg)
                .Where(cutsceneId => cutsceneId != 0
                    && cutsceneSheet.TryGetRow(cutsceneId, out _)
                    && workIndexSheet.TryGetRow(cutsceneId, out var workIndex)
                    && workIndex.WorkIndex != 0)
                .ToArray();
            var previous = quest.PreviousQuest
                .Select(value => value.RowId)
                .Where(value => value != 0)
                .Distinct()
                .ToArray();
            quests.Add(new(
                quest.RowId,
                previous,
                cutscenes));
        }

        var seenCutscenes = quests.SelectMany(quest => quest.CutsceneIds)
            .Distinct()
            .Where(cutsceneId => uiState->IsCutsceneSeen(cutsceneId))
            .ToHashSet();
        return MsqCutsceneSelector.SelectUpcoming(
            quests, frontierQuestId, frontierCompleted, seenCutscenes.Contains, limit);
    }
}
