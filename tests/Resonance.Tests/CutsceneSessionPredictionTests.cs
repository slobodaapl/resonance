using Resonance.Scheduling;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class CutsceneSessionPredictionTests
{
    [Fact]
    public void ReconciliationRetainsPreparedMatchingFutureLines()
    {
        using var session = new CutsceneSession(1, 2);
        var desired = Predictions("a", "b", "c");
        var added = session.ReconcilePredictions(desired);
        var retained = added[1];
        retained.Audio.TryWrite([1f]);
        retained.Audio.Complete();
        Assert.True(retained.TryTransition(DubLineState.Buffered, DubLineState.Predicted));

        Assert.Empty(session.ReconcilePredictions(desired));

        var current = Assert.Single(session.Lines, line => line.PredictionKey == "b");
        Assert.Same(retained, current);
        Assert.Equal(DubLineState.Buffered, current.State);
        Assert.True(current.Audio.ProducerCompleted);
    }

    [Fact]
    public void BranchReconciliationRemovesOnlySkippedPredictions()
    {
        using var session = new CutsceneSession(1, 2);
        var original = session.ReconcilePredictions(Predictions("a", "branch-a", "after"));
        var after = original[2];

        var added = session.ReconcilePredictions(Predictions("a", "branch-b", "after"));

        Assert.Single(added);
        Assert.Equal("branch-b", added[0].PredictionKey);
        Assert.Same(after, Assert.Single(session.Lines, line => line.PredictionKey == "after"));
        Assert.DoesNotContain(session.Lines, line => line.PredictionKey == "branch-a");
    }

    [Fact]
    public void ExactManifestKeyPromotesPreparedCanonicalIdentity()
    {
        using var session = new CutsceneSession(1, 2);
        var predicted = Assert.Single(session.ReconcilePredictions([
            new("cutscene:line", "official:wuk-lamat", "WUKLAMAT", "Ready?", "english", "wuk-lamat"),
        ]));
        predicted.Audio.TryWrite([1f]);
        predicted.Audio.Complete();
        Assert.True(predicted.TryTransition(DubLineState.Buffered, DubLineState.Predicted));
        var assignment = new ResolvedLineSpeaker(
            "official:wuk-lamat", "Wuk Lamat", 42,
            new("official:wuk-lamat"),
            new("tural", [], CastingEvidenceSource.Generic, null, null, 1,
                false, false, [], [], []),
            "feminine", "neutral_adult", 0, Language: "english");

        var actual = session.PromotePrediction(
            "Wuk Lamat", "Ready?", assignment, predictionKey: "cutscene:line");

        Assert.Same(predicted, actual);
        Assert.Equal(DubLineState.Buffered, actual!.State);
        Assert.True(actual.Audio.ProducerCompleted);
        Assert.Equal(ActualStatus.Actual, actual.ActualStatus);
    }

    [Fact]
    public void PreparedTransientMasculineVoiceIsRejectedForResolvedFeminineSpeaker()
    {
        using var session = new CutsceneSession(1, 2);
        var predicted = Assert.Single(session.ReconcilePredictions([
            new("line", "scene:1:npc", "NPC", "Ready?", "english"),
        ]));
        predicted.Casting = new("generic_world", [], CastingEvidenceSource.Generic,
            null, null, 1, false, false, [], [], []);
        predicted.CastingSlotId = "masculine_01";
        predicted.VoiceSex = "masculine";
        predicted.TransientSpeaker = true;
        predicted.Audio.TryWrite([1f]);
        predicted.Audio.Complete();
        Assert.True(predicted.TryTransition(DubLineState.Buffered, DubLineState.Predicted));
        var feminineCasting = new CastingResolution(
            "generic_world", [], CastingEvidenceSource.Generic,
            null, null, 1, false, false, [], [], []);
        var actual = new ResolvedLineSpeaker(
            "scene:1:npc", "NPC", 0, new("scene:1:npc", Sex: "feminine"),
            feminineCasting, "feminine", "feminine_adult", 0, "feminine_01", "english");

        Assert.Null(session.PromotePrediction("NPC", "Ready?", actual, predictionKey: "line"));
        Assert.Equal(DubLineState.Invalidated, predicted.State);
    }

    [Fact]
    public void ReconciliationCarriesObservedSpeakerTraitsIntoFutureLines()
    {
        using var session = new CutsceneSession(1, 2);
        var casting = new CastingResolution(
            "generic_world", [], CastingEvidenceSource.Generic,
            null, null, 1, false, false, [], [], []);
        var resolution = new ResolvedLineSpeaker(
            "npc:42", "NPC", 42, new("npc:42", Sex: "feminine"),
            casting, "feminine", "feminine_adult", 123, "feminine_01", "english");

        var line = Assert.Single(session.ReconcilePredictions([
            new("line", "npc:42", "NPC", "Next", "english", Resolution: resolution),
        ]));

        Assert.Equal("feminine", line.VoiceSex);
        Assert.Equal("feminine_01", line.CastingSlotId);
        Assert.Equal("npc:42", line.SpeakerKey);
        Assert.Equal(42, line.SpeakerId);
    }

    private static CutscenePrediction[] Predictions(params string[] keys) => keys
        .Select(key => new CutscenePrediction(key, $"speaker:{key}", key, $"text:{key}", "english"))
        .ToArray();
}
