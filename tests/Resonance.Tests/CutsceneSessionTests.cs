using Resonance.Scheduling;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class CutsceneSessionTests
{
    [Fact]
    public void PredictionCanNeverBecomeActualWithoutExactObservedMatch()
    {
        using var session = new CutsceneSession(4, 10);
        var predictions = session.ReplacePredictions([
            ("npc:1", "Krile", "Expected"),
            ("npc:2", "Erenville", "Following"),
        ]);

        Assert.Null(session.PromotePrediction("Krile", "Different"));
        Assert.All(predictions, line => Assert.Equal(ActualStatus.Predicted, line.ActualStatus));

        var promoted = session.PromotePrediction("Krile", "Expected");
        Assert.NotNull(promoted);
        Assert.Equal(ActualStatus.Actual, promoted.ActualStatus);
    }

    [Fact]
    public void PromotionOverwritesPredictionWithFreshActualSpeakerEvidence()
    {
        using var session = new CutsceneSession(4, 10);
        session.ReplacePredictions([("predicted:Krile", "Krile", "Expected")]);
        var evidence = new SpeakerCastingEvidence(
            "npc:42", "Ishgard", "Ishgard", 42, "feminine", "humanoid",
            Age: "adult", Physique: "average", BodyType: "average",
            HeightBucket: "average", MuscleMassBucket: "average");
        var casting = new CastingResolution(
            "ishgardian", ["noble"], CastingEvidenceSource.Identity,
            "Ishgard", "Ishgard", 1, false, true, [], ["ishgardian"], []);
        var actual = new ResolvedLineSpeaker(
            "npc:42", "Krile", 42, evidence, casting,
            "feminine", "feminine_adult", new(42, 81, 0x1234, 3), "feminine_03", "japanese");

        var promoted = session.PromotePrediction("Krile", "Expected", actual);

        Assert.NotNull(promoted);
        Assert.Equal("npc:42", promoted.SpeakerKey);
        Assert.Equal(42L, promoted.SpeakerId);
        Assert.Equal("ishgardian", promoted.Casting?.DomainId);
        Assert.Equal("feminine", promoted.VoiceSex);
        Assert.Equal(actual.Actor, promoted.Actor);
        Assert.Equal(evidence, promoted.CastingEvidence);
        Assert.Equal("japanese", promoted.Language);
        Assert.Null(promoted.VoiceProfileId);
    }

    [Theory]
    [InlineData("actual-speaker", true)]
    [InlineData("other-speaker", false)]
    public void PromotionOnlyReusesAudioForTheSameFallbackSpeaker(string predictedKey, bool reuseAudio)
    {
        using var session = new CutsceneSession(4, 10);
        var evidence = new SpeakerCastingEvidence("npc:42");
        var casting = new CastingResolution("generic_world", [], CastingEvidenceSource.Identity,
            null, null, 1, false, false, [], ["generic_world"], []);
        var actual = new ResolvedLineSpeaker("npc:42", "Detective", 42, evidence, casting,
            "masculine", "masculine_adult", null, Language: "english", FallbackSpeakerKey: "actual-speaker");
        var prediction = session.ReconcilePredictions([
            new CutscenePrediction("cue", actual.SpeakerKey, actual.SpeakerName, "Expected", "english",
                Resolution: actual with { FallbackSpeakerKey = predictedKey }),
        ], preserveExisting: true).Single();
        prediction.VoiceProfileId = "profile";
        Assert.True(prediction.TryTransition(DubLineState.Buffered, DubLineState.Predicted));

        var promoted = session.PromotePrediction(actual.SpeakerName, "Expected", actual, predictionKey: "cue");

        if (reuseAudio)
        {
            Assert.NotNull(promoted);
            Assert.Same(prediction, promoted);
            Assert.Equal("profile", promoted.VoiceProfileId);
        }
        else
        {
            Assert.Null(promoted);
            Assert.Equal(DubLineState.Invalidated, prediction.State);
        }
    }

    [Fact]
    public void ReplacingPredictionsInvalidatesOldEpochWork()
    {
        using var session = new CutsceneSession(4, 10);
        var old = session.ReplacePredictions([("npc:1", "Krile", "Old")]).Single();

        session.ReplacePredictions([("npc:2", "Erenville", "New")]);

        Assert.Equal(DubLineState.Invalidated, old.State);
        Assert.True(old.Cancellation.IsCancellationRequested);
        Assert.DoesNotContain(old, session.Lines);
        old.Dispose();
    }

    [Fact]
    public void ReplacingPredictionsPreservesTerminalAndActualLines()
    {
        using var session = new CutsceneSession(4, 10);
        var actual = session.AddActual("npc:actual", "Actual", "Keep actual");
        var terminalPrediction = session.ReplacePredictions([("npc:terminal", "Terminal", "Keep terminal")])[0];
        terminalPrediction.Cancel(DubLineState.Completed);

        session.ReplacePredictions([("npc:new", "New", "Fresh prediction")]);

        Assert.Contains(actual, session.Lines);
        Assert.Contains(terminalPrediction, session.Lines);
        Assert.Equal(DubLineState.Completed, terminalPrediction.State);
    }

    [Fact]
    public void SessionDisposalCancelsActualAndSpeculativeWork()
    {
        var session = new CutsceneSession(7, 9);
        var actual = session.AddActual("npc:1", "Speaker", "Actual");
        var predicted = session.ReplacePredictions([("npc:2", "Other", "Future")])[0];

        session.Dispose();

        Assert.True(actual.Token.IsCancellationRequested);
        Assert.True(predicted.Token.IsCancellationRequested);
        Assert.Empty(session.Lines);
        Assert.Throws<ObjectDisposedException>(() => session.AddActual("npc:3", "Late", "Stale"));
        Assert.Empty(session.ReplacePredictions([("npc:4", "Late", "Stale prediction")]));
    }
}
