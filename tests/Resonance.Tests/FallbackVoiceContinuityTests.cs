using System.Collections.Concurrent;
using Resonance.Game;
using Resonance.Tts;

namespace Resonance.Tests;

public sealed class FallbackVoiceContinuityTests
{
    [Fact]
    public void LaterSceneUsesTheFirstVoiceForTheSameScriptedSpeaker()
    {
        var cache = new ConcurrentDictionary<string, StoredVoiceProfile>();
        var first = MakeProfile("first");
        var next = MakeProfile("next");

        var start = CutsceneVoiceLine.FallbackSpeakerKey("ex5/chrhdb/chrhdb91110/chrhdb91110",
            "HARDBOILED", "Hardboiled");
        var later = CutsceneVoiceLine.FallbackSpeakerKey("ex5/chrhdb/chrhdb93210/chrhdb93210",
            "HARDBOILED", "Hardboiled");
        var unrelated = CutsceneVoiceLine.FallbackSpeakerKey("ex5/other/scene",
            "HARDBOILED", "Hardboiled");
        Assert.NotNull(start);
        Assert.NotNull(later);
        Assert.NotNull(unrelated);

        Assert.Same(first, FallbackVoiceContinuity.Remember(cache, start, first));
        Assert.Same(first, FallbackVoiceContinuity.Remember(cache, later, next));
        Assert.Same(next, FallbackVoiceContinuity.Remember(cache, unrelated, next));
    }

    [Theory]
    [InlineData("UNKNOWN", "???")]
    [InlineData("SYSTEM", "Narrator")]
    [InlineData("002", "Guard")]
    public void AnonymousOrNonSpeakerTokensDoNotGetCrossSceneKeys(string token, string displayName) =>
        Assert.Null(CutsceneVoiceLine.FallbackSpeakerKey("ex5/chrhdb/scene", token, displayName));

    [Fact]
    public void CuratedAndExplicitVoicesBypassAnExistingFallback()
    {
        var cache = new ConcurrentDictionary<string, StoredVoiceProfile>();
        var fallback = MakeProfile("first");
        var official = MakeProfile("official") with { Kind = VoiceProfileKind.Official };
        var custom = MakeProfile("custom") with { SourceMetadata = """{"promptOverride":true}""" };

        FallbackVoiceContinuity.Remember(cache, "speaker", fallback);
        Assert.Same(official, FallbackVoiceContinuity.Remember(cache, "speaker", official));
        Assert.Same(custom, FallbackVoiceContinuity.Remember(cache, "speaker", custom));
        Assert.Same(fallback, cache["speaker"]);
    }

    private static StoredVoiceProfile MakeProfile(string id) =>
        new(id, VoiceProfileKind.Designed, "english", "model", null, null, null,
            new([1f], [1], 1, 1, id), id);
}
