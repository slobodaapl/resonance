using Resonance.Game;

namespace Resonance.Tests;

public sealed class MsqCutsceneSelectorTests
{
    [Theory]
    [InlineData("CUT_SCENE_01")]
    [InlineData("CUTSCENE0")]
    [InlineData("NCUT_EVENT_KINGMF101_01")]
    [InlineData("LOC_CUT_1")]
    [InlineData("LOC_NCUT_01")]
    public void IsCutsceneParameter_AcceptsQuestCutsceneReferences(string instruction)
    {
        Assert.True(MsqCutsceneSelector.IsCutsceneParameter(instruction));
    }

    [Theory]
    [InlineData("EVENTACTION0")]
    [InlineData("TERRITORYTYPE0")]
    [InlineData("LOC_BGM0")]
    [InlineData("LCUT_MOUNT0")]
    [InlineData("LOC_POS_LCUT_SEQ4")]
    public void IsCutsceneParameter_RejectsNumericParameterCollisions(string instruction)
    {
        Assert.False(MsqCutsceneSelector.IsCutsceneParameter(instruction));
    }

    [Fact]
    public void SelectUpcoming_SkipsSeenScenesAndContinuesThroughUniqueSuccessor()
    {
        MsqQuestNode[] quests =
        [
            new(100, [], [10, 11]),
            new(101, [100], [12, 13]),
            new(102, [101], [14]),
        ];

        var actual = MsqCutsceneSelector.SelectUpcoming(
            quests, 100, frontierCompleted: false, cutsceneId => cutsceneId is 10 or 12, 2);

        Assert.Equal([11u, 13u], actual);
    }

    [Fact]
    public void SelectUpcoming_StopsAtAmbiguousQuestBranch()
    {
        MsqQuestNode[] quests =
        [
            new(100, [], [10]),
            new(101, [100], [11]),
            new(102, [100], [12]),
        ];

        var actual = MsqCutsceneSelector.SelectUpcoming(
            quests, 100, frontierCompleted: false, _ => true, 2);

        Assert.Empty(actual);
    }

    [Fact]
    public void SelectUpcoming_RejectsUnknownFrontier()
    {
        MsqQuestNode[] quests =
        [
            new(100, [], [10]),
            new(101, [100], [11]),
        ];

        var actual = MsqCutsceneSelector.SelectUpcoming(
            quests, 999, frontierCompleted: false, _ => false, 2);

        Assert.Empty(actual);
    }

    [Fact]
    public void SelectUpcoming_ContinuesFromLastCompletedMsqAcrossAcceptanceGap()
    {
        MsqQuestNode[] quests =
        [
            new(100, [], [10]),
            new(101, [100], [11, 12]),
        ];

        var actual = MsqCutsceneSelector.SelectUpcoming(
            quests, 100, frontierCompleted: true, _ => false, 2);

        Assert.Equal([11u, 12u], actual);
    }

    [Fact]
    public void SelectUpcoming_RejectsAmbiguousSuccessorAfterCompletedFrontier()
    {
        MsqQuestNode[] quests =
        [
            new(100, [], [10]),
            new(101, [100], [11]),
            new(102, [100], [12]),
        ];

        var actual = MsqCutsceneSelector.SelectUpcoming(
            quests, 100, frontierCompleted: true, _ => false, 2);

        Assert.Empty(actual);
    }

    [Fact]
    public void SelectUpcoming_DeduplicatesScenesAndTerminatesQuestCycles()
    {
        MsqQuestNode[] quests =
        [
            new(100, [101], [10, 10]),
            new(101, [100], [11]),
        ];

        var actual = MsqCutsceneSelector.SelectUpcoming(
            quests, 100, frontierCompleted: false, _ => false, 8);

        Assert.Equal([10u, 11u], actual);
    }
}
