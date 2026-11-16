using Resonance.Game;

namespace Resonance.Tests;

public sealed class QuestDialogueMatcherTests
{
    [Fact]
    public void NativeActorIdCarriesIdentityAcrossLocalizedAndMisleadingNames()
    {
        var expected = new QuestDialogueLine("quest/a", 2, "WUKLAMAT", "Our next line.");
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "Begin."), new("quest/a", 1, "WUKLAMAT", "Yes."), expected],
            [new("quest/b", 0, "ALISAIE", "Yes."), new("quest/b", 1, "ALISAIE", "Wrong quest.")],
        ]);
        Assert.True(matcher.Observe("ウクラマト", "Begin.", actorKey: "npc:1047302").Synchronized);
        Assert.Same(expected, Assert.Single(matcher.Observe("Alisaie", "Yes.", actorKey: "npc:1047302").Future));
    }

    [Fact]
    public void UnboundNativeActorDoesNotUseDisplayNameToGuessAmbiguousDialogue()
    {
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "Yes.")],
            [new("quest/b", 0, "ALISAIE", "Yes.")],
        ]);
        Assert.False(matcher.Observe("WUKLAMAT", "Yes.", actorKey: "npc:999").Synchronized);
    }

    [Theory]
    [InlineData("Wuk Lamat", "WUKLAMAT")]
    [InlineData("Emet-Selch", "EMETSELCH")]
    [InlineData("ウクラマト", "WUKLAMAT")]
    [InlineData("Archbishop Thordan VII", "THORDAN")]
    public void DisplayNamesResolveExactQuestDialogue(string displayed, string actor)
    {
        var next = new QuestDialogueLine("quest/a", 1, actor, "Next.");
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, actor, "Begin."), next],
        ]);

        var update = matcher.Observe(displayed, "Begin.");

        Assert.True(update.Synchronized);
        Assert.False(update.Resynchronized);
        Assert.Same(next, Assert.Single(update.Future));
    }

    [Fact]
    public void NormalizedActorDisambiguatesIdenticalTextAcrossQuests()
    {
        var expected = new QuestDialogueLine("quest/a", 1, "WUKLAMAT", "Our next line.");
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "Yes."), expected],
            [new("quest/b", 0, "ALISAIE", "Yes."), new("quest/b", 1, "ALISAIE", "Wrong quest.")],
        ]);

        Assert.Same(expected, Assert.Single(matcher.Observe("Wuk Lamat", "Yes.").Future));
    }

    [Fact]
    public void UnknownSpeakerCannotChooseBetweenIdenticalDialogue()
    {
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "Yes.")],
            [new("quest/b", 0, "ALISAIE", "Yes.")],
        ]);

        var update = matcher.Observe("Unknown", "Yes.");

        Assert.False(update.Synchronized);
        Assert.Empty(update.Future);
    }

    [Fact]
    public void SameActorAndTextCannotChooseBetweenUnanchoredQuests()
    {
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "Yes.")],
            [new("quest/b", 0, "WUKLAMAT", "Yes.")],
        ]);

        Assert.False(matcher.Observe("Wuk Lamat", "Yes.").Synchronized);
    }

    [Fact]
    public void ActiveQuestCannotHideAnotherActorsIdenticalDialogue()
    {
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "Begin."), new("quest/a", 1, "WUKLAMAT", "Yes.")],
            [new("quest/b", 0, "ALISAIE", "Yes.")],
        ]);
        Assert.True(matcher.Observe("WUKLAMAT", "Begin.").Synchronized);

        var update = matcher.Observe("Unknown", "Yes.");

        Assert.False(update.Synchronized);
        Assert.True(update.Resynchronized);
        Assert.Empty(update.Future);
    }

    [Fact]
    public void CompletedQuestCannotStaySynchronizedWithUnrelatedDialogue()
    {
        var matcher = new QuestDialogueMatcher([[new("quest/a", 0, "WUKLAMAT", "Last.")]]);
        Assert.True(matcher.Observe("WUKLAMAT", "Last.").Synchronized);

        var update = matcher.Observe("WUKLAMAT", "Not in this quest.");

        Assert.False(update.Synchronized);
        Assert.True(update.Resynchronized);
        Assert.Empty(update.Future);
    }

    [Fact]
    public void SkippedDialogueResynchronizesAndHonorsLookaheadLimit()
    {
        var expected = new QuestDialogueLine("quest/a", 3, "WUKLAMAT", "Fourth.");
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "First."), new("quest/a", 1, "WUKLAMAT", "Second."),
                new("quest/a", 2, "WUKLAMAT", "Third."), expected, new("quest/a", 4, "WUKLAMAT", "Fifth.")],
        ]);
        Assert.True(matcher.Observe("WUKLAMAT", "First.").Synchronized);

        var update = matcher.Observe("WUKLAMAT", "Third.", lookahead: 1);

        Assert.True(update.Synchronized);
        Assert.True(update.Resynchronized);
        Assert.Same(expected, Assert.Single(update.Future));
        Assert.Empty(matcher.Observe("WUKLAMAT", "Fourth.", lookahead: 0).Future);
    }

    [Fact]
    public void SpeakerLabelAndWhitespaceDoNotChangeDialogueIdentity()
    {
        var matcher = new QuestDialogueMatcher([
            [new("quest/a", 0, "WUKLAMAT", "(-Third Promise-)Let us\n go.")],
        ]);

        Assert.True(matcher.Observe("Wuk Lamat", "Let us go.").Synchronized);
    }

    [Fact]
    public void DisplayedTitleCanSpanDifferentInternalTokens()
    {
        var next = new QuestDialogueLine("quest/a", 1, "THIRDPROMISE", "Another line.");
        var matcher = new QuestDialogueMatcher([[new("quest/a", 0, "WUKLAMAT", "Onward."), next]]);

        Assert.Same(next, Assert.Single(matcher.Observe("Third Promise", "Onward.").Future));
    }
}
