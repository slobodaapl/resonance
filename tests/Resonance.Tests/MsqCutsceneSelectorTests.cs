using Resonance.Game;

namespace Resonance.Tests;

public sealed class MsqCutsceneSelectorTests
{
    [Fact]
    public void AcceptedReplayOverridesNormalTrackerAndHistoricalSeenFlags()
    {
        MsqQuestNode[] quests =
        [
            new(67198, [], [1039]),
            new(67199, [67198], [888, 889]),
            new(70000, [], [9000]),
        ];
        var frontier = MsqCutsceneSelector.ResolveFrontier(
            quests, new HashSet<uint> { 67198, 70000 }, 70000, 69999, id => id == 67198);

        Assert.Equal(new MsqQuestFrontier(67198, false, true), frontier);
        Assert.Equal([1039u, 888u], MsqCutsceneSelector.SelectUpcoming(
            quests, frontier!, _ => true, 2));
    }

    [Fact]
    public void AcceptedNormalQuestWorksWithoutTrackerAndSkipsSeenScenes()
    {
        MsqQuestNode[] quests = [new(100, [], [10, 11])];
        var frontier = MsqCutsceneSelector.ResolveFrontier(
            quests, new HashSet<uint> { 100, 500 }, 0, 0, _ => false);

        Assert.Equal(new MsqQuestFrontier(100, false, false), frontier);
        Assert.Equal([11u], MsqCutsceneSelector.SelectUpcoming(
            quests, frontier!, id => id == 10, 2));
    }

    [Fact]
    public void SameZoneAcceptanceAndCompletionMoveThePreparationFrontier()
    {
        MsqQuestNode[] quests = [new(100, [], [10]), new(101, [100], [11])];
        var accepted = new HashSet<uint> { 100 };
        var first = MsqCutsceneSelector.ResolveFrontier(quests, accepted, 100, 0, _ => false);
        Assert.Equal([10u], MsqCutsceneSelector.SelectUpcoming(quests, first!, _ => false, 1));

        accepted.Clear();
        var gap = MsqCutsceneSelector.ResolveFrontier(quests, accepted, 0, 100, id => id == 100);
        Assert.Equal(new MsqQuestFrontier(100, true, false), gap);
        Assert.Equal([11u], MsqCutsceneSelector.SelectUpcoming(quests, gap!, _ => false, 1));

        accepted.Add(101);
        var next = MsqCutsceneSelector.ResolveFrontier(quests, accepted, 0, 100, id => id == 100);
        Assert.Equal(new MsqQuestFrontier(101, false, false), next);
        Assert.Equal([11u], MsqCutsceneSelector.SelectUpcoming(quests, next!, _ => false, 1));
    }

    [Fact]
    public void MultipleAcceptedPathsRequireTheTrackedPath()
    {
        MsqQuestNode[] quests = [new(100, [], [10]), new(200, [], [20])];
        var accepted = new HashSet<uint> { 100, 200 };

        Assert.Null(MsqCutsceneSelector.ResolveFrontier(quests, accepted, 0, 99, _ => false));
        Assert.Equal(new MsqQuestFrontier(200, false, false),
            MsqCutsceneSelector.ResolveFrontier(quests, accepted, 200, 99, _ => false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlayingUntrackedScenesMovesThePreparationWindowForward(bool replay)
    {
        MsqQuestNode[] quests = [new(100, [], [10, 11, 12, 13])];
        var frontier = new MsqQuestFrontier(100, false, replay);
        var played = new HashSet<uint>();
        Assert.Equal([10u, 11u], MsqCutsceneSelector.SelectUpcoming(
            quests, frontier, _ => false, 2, played));

        played.Add(10);
        Assert.Equal([11u, 12u], MsqCutsceneSelector.SelectUpcoming(
            quests, frontier, _ => false, 2, played));

        played.Add(11);
        Assert.Equal([12u, 13u], MsqCutsceneSelector.SelectUpcoming(
            quests, frontier, _ => false, 2, played));
    }

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
