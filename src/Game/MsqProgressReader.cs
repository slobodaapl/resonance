using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace Resonance.Game;

public sealed class MsqProgressReader : IAsyncDisposable
{
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly MsqQuestCatalog catalog;
    private IReadOnlyList<MsqQuestNode>? quests;
    private MsqQuestFrontier? frontier;
    private readonly HashSet<uint> playedCutscenes = [];

    public MsqProgressReader(IDataManager dataManager, IClientState clientState)
    {
        this.dataManager = dataManager;
        this.clientState = clientState;
        catalog = new(language => LoadQuests((Dalamud.Game.ClientLanguage)language));
    }

    public unsafe IReadOnlyList<uint> GetUpcomingCutscenes(int limit)
    {
        if (limit <= 0) return [];
        var current = ReadFrontier();
        var uiState = UIState.Instance();
        return current is null || uiState == null ? [] : MsqCutsceneSelector.SelectUpcoming(
            quests!, current, cutsceneId => uiState->IsCutsceneSeen(cutsceneId), limit, playedCutscenes);
    }

    public void ObserveCutscene(uint cutsceneId)
    {
        if (cutsceneId != 0 && ReadFrontier() is not null) playedCutscenes.Add(cutsceneId);
    }

    public void Reset()
    {
        frontier = null;
        playedCutscenes.Clear();
    }

    private unsafe MsqQuestFrontier? ReadFrontier()
    {
        var manager = QuestManager.Instance();
        var scenarioTree = AgentScenarioTree.Instance();
        if (manager == null) return null;

        var scenario = scenarioTree == null ? null : scenarioTree->Data;
        var pathQuestId = scenario != null && scenario->MSQPathIndex < 3
            ? scenario->MainScenarioQuestIds[scenario->MSQPathIndex]
            : (ushort)0;
        quests = catalog.TryGet((int)clientState.ClientLanguage);
        if (quests is null) return null;
        var acceptedQuestIds = new HashSet<uint>();
        foreach (ref var work in manager->NormalQuests)
            if (work.QuestId != 0) acceptedQuestIds.Add((uint)work.QuestId + 0x10000);
        var current = MsqCutsceneSelector.ResolveFrontier(quests, acceptedQuestIds,
            pathQuestId == 0 ? 0 : (uint)pathQuestId + 0x10000,
            scenario == null || scenario->MainScenarioQuestIds[3] == 0
                ? 0 : (uint)scenario->MainScenarioQuestIds[3] + 0x10000,
            QuestManager.IsQuestComplete);
        if (frontier != current)
        {
            playedCutscenes.Clear();
            frontier = current;
        }
        return current;
    }

    private IReadOnlyList<MsqQuestNode> LoadQuests(Dalamud.Game.ClientLanguage language)
    {
        var questSheet = dataManager.GetExcelSheet<Quest>(language);
        var cutsceneSheet = dataManager.GetExcelSheet<Cutscene>(language);
        var quests = new List<MsqQuestNode>();
        foreach (var quest in questSheet)
        {
            if (quest.EventIconType.RowId != 3) continue;
            var cutscenes = quest.QuestParams
                .Where(parameter => MsqCutsceneSelector.IsCutsceneParameter(
                    parameter.ScriptInstruction.ExtractText()))
                .Select(parameter => parameter.ScriptArg)
                .Where(cutsceneId => cutsceneId != 0
                    && cutsceneSheet.TryGetRow(cutsceneId, out _))
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

        return quests;
    }

    public ValueTask DisposeAsync() => catalog.DisposeAsync();
}
