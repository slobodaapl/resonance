using System.Text;
using Resonance.Game;

namespace Resonance.Tests;

public sealed class CutsceneVoiceManifestTests
{
    [Fact]
    public void NativeRowIdentitySurvivesFormattedTextAndContradictoryDisplayNames()
    {
        var native = new CutsceneVoiceLine("native-key", "THORDAN", "Welcome, <player>.", true, null, 0, 0);
        var synthetic = new CutsceneVoiceLine("undubbed-key", "THORDAN", "Welcome, <player>.", false, null, 1, 1);
        var manifest = new CutsceneVoiceManifest(1, "scene", [native, synthetic]);
        var observed = manifest.MatchFrontier("Another speaker", "Welcome, Jack.", [], rowKey: "native-key");
        Assert.Same(native, Assert.Single(observed));
        Assert.Same(synthetic, Assert.Single(manifest.MatchFrontier("Another speaker", "Welcome, Jack.", [], rowKey: "undubbed-key")));
        Assert.Empty(manifest.MatchFrontier("THORDAN", native.Text, [], rowKey: "missing-key"));
    }

    [Fact]
    public void NativeNpcIdOverridesMisleadingDisplayName()
    {
        var thordan = new CutsceneVoiceLine("thordan", "THORDAN", "Yes.", true, null, 0, 0,
            ActorNpcBaseId: 1012103);
        var other = new CutsceneVoiceLine("other", "ALISAIE", "Yes.", false, null, 1, 1,
            ActorNpcBaseId: 1008182);
        var manifest = new CutsceneVoiceManifest(856, "test", [thordan, other]);
        Assert.Same(thordan, Assert.Single(manifest.MatchFrontier("Alisaie", "Yes.", [], 1012103)));
        Assert.Empty(manifest.MatchFrontier("THORDAN", "Yes.", [], 999));
        Assert.False(manifest.HasOnlyNativeDialogue("Yes.", 999));
        Assert.True(manifest.HasOnlyNativeDialogue("Yes.", 1012103));
    }

    [Fact]
    public void ObservedNpcBindingDisambiguatesPackedActorHashes()
    {
        var native = new CutsceneVoiceLine("native", "", "Yes.", true, null, 0, 0,
            ActorTokenHash: "thordan-hash");
        var synthetic = new CutsceneVoiceLine("synthetic", "", "Yes.", false, null, 1, 1,
            ActorTokenHash: "alisaie-hash");
        var manifest = new CutsceneVoiceManifest(856, "test", [native, synthetic]);
        Assert.Same(native, Assert.Single(manifest.MatchFrontier("Alisaie", "Yes.", [], 1012103,
            new HashSet<string> { "thordan-hash" })));
        Assert.Empty(manifest.MatchFrontier("Alisaie", "Yes.", [], 1012103));
        Assert.Same(native, Assert.Single(manifest.MatchFrontier("", "Yes.", [], null,
            new HashSet<string> { "thordan-hash" })));
    }

    [Theory]
    [InlineData("Archbishop Thordan VII", "Go. Azys Lla awaits.", false)]
    [InlineData("Archbishop Thordan VII", "Go. Azys Lla awaits.", true)]
    [InlineData("トールダン7世", "行け……「魔大陸」へ……。", false)]
    [InlineData("トールダン7世", "行け……「魔大陸」へ……。", true)]
    [InlineData("Papst Thordan VII.", "Los! Auf nach Azys Lla!", false)]
    [InlineData("Papst Thordan VII.", "Los! Auf nach Azys Lla!", true)]
    [InlineData("archevêque Thordan VII", "En route pour Azys Lla...", false)]
    [InlineData("archevêque Thordan VII", "En route pour Azys Lla...", true)]
    public void ThordanNativeDialogueSurvivesTitledAndLocalizedNames(
        string speaker, string text, bool packed)
    {
        var line = new CutsceneVoiceLine(
            "TEXT_VOICEMAN_03004_600090_THORDAN", packed ? "thordan" : "THORDAN",
            text, true, null, 8, 8,
            OfficialGroupId: packed ? "thordan" : null,
            ActorTokenHash: packed
                ? "e0d5052806315cc203c24cfbc7ec7b526fd4a859b7ff76d240fca28898d0c3e2"
                : null);
        var manifest = new CutsceneVoiceManifest(856, "ex1/test", [line]);

        var matched = Assert.Single(manifest.MatchFrontier(speaker, text, [line.NodeId]));

        Assert.True(matched.IsVoiced);
        Assert.False(matched.IsPlayerChoice);
        Assert.Same(line, matched);
    }

    [Fact]
    public void LocalizedRepeatedDialogueKeepsTheCurrentOccurrence()
    {
        var first = new CutsceneVoiceLine("first", "WUKLAMAT", "進もう。", true, null, 0, 0);
        var second = new CutsceneVoiceLine("second", "WUKLAMAT", "進もう。", true, null, 1, 1);
        var manifest = new CutsceneVoiceManifest(1, "test", [first, second]);

        Assert.Same(second, Assert.Single(manifest.MatchFrontier("ウクラマト", "進もう。", [second.NodeId])));
    }

    [Fact]
    public void FrontierCannotHideAnotherActorsIdenticalDialogue()
    {
        var native = new CutsceneVoiceLine("native", "WUKLAMAT", "Yes.", true, null, 0, 0);
        var synthetic = new CutsceneVoiceLine("synthetic", "ALISAIE", "Yes.", false, null, 1, 1);
        var manifest = new CutsceneVoiceManifest(1, "test", [native, synthetic]);

        Assert.Empty(manifest.MatchFrontier("Unknown", "Yes.", [native.NodeId]));
        Assert.Same(synthetic, Assert.Single(manifest.MatchFrontier("Alisaie", "Yes.", [])));
    }

    [Fact]
    public void DisplayedTitleCanSpanDifferentInternalTokens()
    {
        var native = new CutsceneVoiceLine("native", "WUKLAMAT", "Onward.", true, null, 0, 0);
        var synthetic = new CutsceneVoiceLine("synthetic", "THIRDPROMISE", "Another line.", false, null, 1, 1);
        var manifest = new CutsceneVoiceManifest(1, "test", [native, synthetic]);

        Assert.Same(native, Assert.Single(manifest.MatchFrontier("Third Promise", "Onward.", [])));
    }

    [Fact]
    public void DisplayNameFallbackCannotGuessBetweenNativeAndSyntheticOccurrences()
    {
        var native = new CutsceneVoiceLine("native", "THORDAN", "Yes.", true, null, 0, 0);
        var synthetic = new CutsceneVoiceLine("synthetic", "THORDAN", "Yes.", false, null, 1, 1);
        var manifest = new CutsceneVoiceManifest(1, "test", [native, synthetic]);

        Assert.Empty(manifest.MatchFrontier("Archbishop Thordan VII", "Yes.", [native.NodeId]));
    }

    [Fact]
    public void UnattributedPlayerChoiceStillMatchesForAKnownDisplayedSpeaker()
    {
        var native = new CutsceneVoiceLine("native", "WUKLAMAT", "First.", true, null, 0, 0);
        var choice = new CutsceneVoiceLine("choice", "NONE_VOICE", "What will you say?", false, null, 1, 1,
            IsPlayerChoice: true);
        var manifest = new CutsceneVoiceManifest(1, "test", [native, choice]);

        Assert.Same(choice, Assert.Single(manifest.MatchFrontier("Wuk Lamat", "What will you say?", [choice.NodeId])));
    }

    [Fact]
    public void LocalizedUnvoicedDialogueKeepsItsActorAndSyntheticStatus()
    {
        var line = new CutsceneVoiceLine("quest-line", "WUKLAMAT", "進もう。", false, null, 0, 0);
        var manifest = new CutsceneVoiceManifest(1, "test", [line]);

        var match = Assert.Single(manifest.MatchFrontier("ウクラマト", "進もう。", [line.NodeId]));

        Assert.False(match.IsVoiced);
        Assert.Equal("WUKLAMAT", match.ActorToken);
    }

    [Fact]
    public void UnrecognizedDialogueCannotBorrowTheFrontiersNativeStatus()
    {
        var native = new CutsceneVoiceLine("native", "THORDAN", "Go. Azys Lla awaits.", true, null, 0, 0);
        var manifest = new CutsceneVoiceManifest(856, "test", [native]);

        Assert.Empty(manifest.MatchFrontier("Archbishop Thordan VII", "A different line.", [native.NodeId]));
        Assert.False(manifest.HasOnlyNativeDialogue("A different line."));
    }

    [Fact]
    public void NativeVoiceCanBeCertainWhileSpeakerIdentityIsAmbiguous()
    {
        var first = new CutsceneVoiceLine("first", "WUKLAMAT", "Yes.", true, null, 0, 0);
        var second = new CutsceneVoiceLine("second", "ALISAIE", "Yes.", true, null, 1, 1);
        var manifest = new CutsceneVoiceManifest(1, "test", [first, second]);

        Assert.Empty(manifest.MatchFrontier("Unknown", "Yes.", []));
        Assert.True(manifest.HasOnlyNativeDialogue("Yes."));
    }

    [Fact]
    public void MixedVoiceStatusCannotBeClassifiedAsNativeWithoutAnActorMatch()
    {
        var native = new CutsceneVoiceLine("native", "WUKLAMAT", "Yes.", true, null, 0, 0);
        var synthetic = new CutsceneVoiceLine("synthetic", "ALISAIE", "Yes.", false, null, 1, 1);
        var manifest = new CutsceneVoiceManifest(1, "test", [native, synthetic]);

        Assert.False(manifest.HasOnlyNativeDialogue("Yes."));
        Assert.True(Assert.Single(manifest.MatchFrontier("Wuk Lamat", "Yes.", [])).IsVoiced);
        Assert.False(Assert.Single(manifest.MatchFrontier("Alisaie", "Yes.", [])).IsVoiced);
    }

    [Fact]
    public void CutbVoiceKeysResolveExactActorTextAndAudioResource()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "binary-prefix",
            "cut_scene/070/VoiceMan_07000",
            "TEXT_VOICEMAN_07000_000010_WUKLAMAT",
            "TEXT_VOICEMAN_07000_000020_ALISAIE",
            "binary-suffix"));
        var sheet = new Dictionary<string, string>
        {
            ["TEXT_VOICEMAN_07000_000010_WUKLAMAT"] = "Savoring the moment?",
            ["TEXT_VOICEMAN_07000_000020_ALISAIE"] = "Well, for once...",
        };

        var manifest = CutsceneVoiceManifestParser.Parse(
            3265, "ex5/kinact/kinact01010/kinact01010", cutb,
            name => name == "cut_scene/070/VoiceMan_07000" ? sheet : null,
            "en");

        var line = Assert.Single(manifest.Lines, value => value.ActorToken == "WUKLAMAT");
        Assert.True(line.IsVoiced);
        Assert.Equal("Savoring the moment?", line.Text);
        Assert.Equal(
            "cut/ex5/sound/voicem/voiceman_07000/vo_voiceman_07000_000010_m_en.scd",
            line.ScdPath);
        Assert.Same(line, manifest.Match("Wuk Lamat", "Savoring  the moment?"));
    }

    [Fact]
    public void ExplicitNoneVoiceKeyNeverClaimsNativeDubbing()
    {
        const string key = "TEXT_VOICEMAN_07000_Q1_000_001_NONE_VOICE";
        var cutb = Encoding.UTF8.GetBytes(
            $"cut_scene/070/VoiceMan_07000\0{key}\0");
        var manifest = CutsceneVoiceManifestParser.Parse(
            3272, "ex5/kinact/kinact01080/kinact01080", cutb,
            _ => new Dictionary<string, string> { [key] = "What will you say?" },
            "en");

        var line = Assert.Single(manifest.Lines);
        Assert.False(line.IsVoiced);
        Assert.Equal("NONE_VOICE", line.ActorToken);
        Assert.Null(line.ScdPath);
    }

    [Fact]
    public void DuplicateDialogueRequiresActorDisambiguation()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "cut_scene/070/VoiceMan_07000",
            "TEXT_VOICEMAN_07000_000010_WUKLAMAT",
            "TEXT_VOICEMAN_07000_000020_ALISAIE"));
        var sheet = new Dictionary<string, string>
        {
            ["TEXT_VOICEMAN_07000_000010_WUKLAMAT"] = "Yes.",
            ["TEXT_VOICEMAN_07000_000020_ALISAIE"] = "Yes.",
        };
        var manifest = CutsceneVoiceManifestParser.Parse(
            3265, "ex5/kinact/kinact01010/kinact01010", cutb, _ => sheet, "en");

        Assert.Null(manifest.Match("Unknown", "Yes."));
        Assert.Equal("WUKLAMAT", manifest.Match("Wuk Lamat", "Yes.")?.ActorToken);
    }

    [Fact]
    public void QuestCutsceneKeysProvideExactSpeakerAndSemanticOrder()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "quest/000/ClsArc001_00046",
            "TEXT_CLSARC001_00046_LUCIANE_000_0020",
            "TEXT_CLSARC001_00046_LEIHALIAPOH_000_0019",
            "TEXT_CLSARC001_00046_LUCIANE_000_0001"));
        var sheet = new Dictionary<string, string>
        {
            ["TEXT_CLSARC001_00046_LUCIANE_000_0020"] = "Last",
            ["TEXT_CLSARC001_00046_LEIHALIAPOH_000_0019"] = "Middle",
            ["TEXT_CLSARC001_00046_LUCIANE_000_0001"] = "First",
        };

        var manifest = CutsceneVoiceManifestParser.Parse(
            10, "ffxiv/clsarc/clsarc00110/clsarc00110", cutb,
            name => name == "quest/000/ClsArc001_00046" ? sheet : null, "en");

        Assert.Equal(["First", "Middle", "Last"], manifest.Lines.Select(line => line.Text));
        Assert.Equal("LUCIANE", manifest.Lines[0].ActorToken);
        Assert.False(manifest.Lines[0].IsVoiced);
        Assert.Equal(1, manifest.Lines[0].Ordinal);
    }

    [Fact]
    public void RepeatedActorTextAdvancesFromManifestCursor()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "quest/000/Test_00001",
            "TEXT_TEST_00001_NPC_000_0001",
            "TEXT_TEST_00001_NPC_000_0002"));
        var sheet = new Dictionary<string, string>
        {
            ["TEXT_TEST_00001_NPC_000_0001"] = "Again.",
            ["TEXT_TEST_00001_NPC_000_0002"] = "Again.",
        };
        var manifest = CutsceneVoiceManifestParser.Parse(
            1, "ffxiv/test/test", cutb, _ => sheet, "en");

        var first = manifest.Match("NPC", "Again.");
        var second = manifest.Match("NPC", "Again.", first!.Order);

        Assert.NotEqual(first.Key, second!.Key);
        Assert.True(second.Order > first.Order);
    }

    [Theory]
    [InlineData("TEXT_MANFST000_00083_Q2_000_0001")]
    [InlineData("TEXT_MANFST000_00083_A1_000_0001")]
    public void QuestChoiceRowsAreBarriersNotSyntheticSpeakers(string key)
    {
        var cutb = Encoding.UTF8.GetBytes($"quest/000/ManFst000_00083\0{key}\0");
        var manifest = CutsceneVoiceManifestParser.Parse(
            3, "ffxiv/manfst/manfst00010/manfst00010", cutb,
            _ => new Dictionary<string, string> { [key] = "Choice text" }, "en");

        var line = Assert.Single(manifest.Lines);
        Assert.True(line.IsPlayerChoice);
        Assert.False(line.IsVoiced);
    }

    [Fact]
    public void BranchSiblingIsNotTreatedAsTheSuccessorOfChosenLine()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "quest/000/Test_00001",
            "TEXT_TEST_00001_NPCONE_000_0001",
            "TEXT_TEST_00001_NPCTWO_000_0001",
            "TEXT_TEST_00001_NPCTHREE_000_0002"));
        var sheet = new Dictionary<string, string>
        {
            ["TEXT_TEST_00001_NPCONE_000_0001"] = "Branch one",
            ["TEXT_TEST_00001_NPCTWO_000_0001"] = "Branch two",
            ["TEXT_TEST_00001_NPCTHREE_000_0002"] = "Rejoined",
        };
        var manifest = CutsceneVoiceManifestParser.Parse(
            1, "ffxiv/test/test", cutb, _ => sheet, "en");
        var chosen = manifest.Lines.Single(line => line.Text == "Branch one");

        Assert.Equal("Rejoined", manifest.ImmediateSuccessor(chosen)?.Text);
        Assert.Equal(["Rejoined"], manifest.SyntheticFuture(chosen).Select(line => line.Text));
    }

    [Fact]
    public void DuplicateOrdinalCreatesDiamondFrontierAndExactBubbleSelectsBranch()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "quest/000/Test_00001",
            "TEXT_TEST_00001_NPCONE_000_0001",
            "TEXT_TEST_00001_NPCTWO_000_0001",
            "TEXT_TEST_00001_NPCTHREE_000_0002"));
        var manifest = CutsceneVoiceManifestParser.Parse(
            1, "ffxiv/test/test", cutb, _ => new Dictionary<string, string>
            {
                ["TEXT_TEST_00001_NPCONE_000_0001"] = "Left",
                ["TEXT_TEST_00001_NPCTWO_000_0001"] = "Right",
                ["TEXT_TEST_00001_NPCTHREE_000_0002"] = "Merge",
            }, "en");

        Assert.Equal(2, manifest.StartNodes.Count);
        var chosen = Assert.Single(manifest.MatchFrontier(
            "NPC Two", "Right", manifest.StartNodes.Select(line => line.NodeId).ToArray()));
        Assert.Equal("NPCTWO", chosen.ActorToken);
        Assert.Equal("Merge", Assert.Single(manifest.Successors([chosen])).Text);
    }

    [Fact]
    public void SameTextDifferentActorsRemainsAmbiguousWithoutExactActor()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "quest/000/Test_00001",
            "TEXT_TEST_00001_NPCONE_000_0001",
            "TEXT_TEST_00001_NPCTWO_000_0001"));
        var manifest = CutsceneVoiceManifestParser.Parse(
            1, "ffxiv/test/test", cutb, _ => new Dictionary<string, string>
            {
                ["TEXT_TEST_00001_NPCONE_000_0001"] = "Same",
                ["TEXT_TEST_00001_NPCTWO_000_0001"] = "Same",
            }, "en");

        Assert.Empty(manifest.MatchFrontier(
            "Unknown", "Same", manifest.StartNodes.Select(line => line.NodeId).ToArray()));
    }

    [Fact]
    public void SheetSpeakerLabelDoesNotCauseNativeVoiceMiss()
    {
        const string key = "TEXT_VOICEMAN_07000_000010_WUKLAMAT";
        var cutb = Encoding.UTF8.GetBytes($"cut_scene/070/VoiceMan_07000\0{key}\0");
        var manifest = CutsceneVoiceManifestParser.Parse(
            3265, "ex5/kinact/kinact01010/kinact01010", cutb,
            _ => new Dictionary<string, string>
            {
                [key] = "(-Third Promise-)Savoring the moment?",
            }, "en");

        Assert.True(manifest.Match("Wuk Lamat", "Savoring the moment?")?.IsVoiced);
    }

    [Fact]
    public void SequenceFallbackRefusesAmbiguousBranch()
    {
        var cutb = Encoding.UTF8.GetBytes(String.Join('\0',
            "quest/000/Test_00001",
            "TEXT_TEST_00001_NPCONE_000_0001",
            "TEXT_TEST_00001_NPCTWO_000_0001"));
        var manifest = CutsceneVoiceManifestParser.Parse(
            1, "ffxiv/test/test", cutb, _ => new Dictionary<string, string>
            {
                ["TEXT_TEST_00001_NPCONE_000_0001"] = "One",
                ["TEXT_TEST_00001_NPCTWO_000_0001"] = "Two",
            }, "en");

        Assert.Null(manifest.Match("Unknown", "Runtime-substituted text"));
    }
}
