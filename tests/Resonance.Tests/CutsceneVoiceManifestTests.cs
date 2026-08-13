using System.Text;
using Resonance.Game;

namespace Resonance.Tests;

public sealed class CutsceneVoiceManifestTests
{
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
