using System.Buffers.Binary;
using System.Text;
using Resonance.Game;

namespace Resonance.Tests;

public sealed class CutsceneFlowTests
{
    private const string First = "TEXT_FIXTURE_00001_FIRST_000_900";
    private const string Second = "TEXT_FIXTURE_00001_SECOND_000_100";
    private const string Choice = "TEXT_FIXTURE_00001_Q1_000_200";
    private const string Sheet = "dungeon/000/Fixture_00000";

    [Fact]
    public void SequenceTargetsAndFrameTimesOverrideNumericTextOrder()
    {
        var bytes = Build([
            new(0, [new(First, 30), new(Second, 10)], [new(true, [2])]),
            new(1, [], []),
            new(2, [new(Choice, 1, Choice: true)], [new(true, [1])]),
        ]);
        var flow = CutsceneFlowParser.Parse(bytes)!;
        var first = flow.Cues.Single(cue => cue.Key == First);
        var second = flow.Cues.Single(cue => cue.Key == Second);
        var choice = flow.Cues.Single(cue => cue.Key == Choice);
        Assert.Contains(new(null, second.Offset), flow.Edges);
        Assert.Contains(new(second.Offset, first.Offset), flow.Edges);
        Assert.Contains(new(first.Offset, choice.Offset), flow.Edges);
        Assert.Contains(new(choice.Offset, null), flow.Edges);
        Assert.DoesNotContain(new(first.Offset, second.Offset), flow.Edges);
        Assert.Equal([Sheet], CutsceneVoiceManifestParser.ExtractDialogueSheetNames(bytes));
    }

    [Fact]
    public void FirstTrueBranchWinsAndLoopsRemainLoops()
    {
        var bytes = Build([
            new(0, [new(First, 1)], [new(false, [1]), new(true, [2]), new(true, [3])]),
            new(2, [new("false-branch", 1)], []),
            new(2, [new(Second, 1)], [new(true, [0])]),
            new(2, [new("unreachable-branch", 1)], []),
        ]);
        var flow = CutsceneFlowParser.Parse(bytes)!;
        var first = flow.Cues.Single(cue => cue.Key == First);
        var second = flow.Cues.Single(cue => cue.Key == Second);
        Assert.Equal([new(first.Offset, second.Offset)], flow.Edges.Where(edge => edge.CurrentOffset == first.Offset));
        Assert.Equal([new(second.Offset, first.Offset)], flow.Edges.Where(edge => edge.CurrentOffset == second.Offset));
    }

    [Fact]
    public void BranchTargetsUseRuntimeIdsAssignedByTheNativeLoader()
    {
        var bytes = Build([
            new(0, [new(First, 1)], [new(true, [1])], Id: 42),
            new(2, [new(Second, 1)], [], Id: 70),
        ]);
        var flow = CutsceneFlowParser.Parse(bytes)!;
        var first = flow.Cues.Single(cue => cue.Key == First);
        var second = flow.Cues.Single(cue => cue.Key == Second);
        Assert.Contains(new(null, first.Offset), flow.Edges);
        Assert.Contains(new(first.Offset, second.Offset), flow.Edges);
    }

    [Fact]
    public void UnknownBranchRetainsBothOutcomesAndFinalBranchIsTheDefault()
    {
        var bytes = Build([
            new(0, [new(First, 1)], [new(null, [1]), new(false, [2])]),
            new(2, [new(Second, 1)], []),
            new(2, [new(Choice, 1, Choice: true)], []),
        ]);
        var flow = CutsceneFlowParser.Parse(bytes)!;
        var first = flow.Cues.Single(cue => cue.Key == First);
        Assert.Equal(2, flow.Edges.Count(edge => edge.CurrentOffset == first.Offset));
    }

    [Fact]
    public void DisabledAndUnreferencedTracksCannotCreateDialoguePredictions()
    {
        var bytes = Build([new(0, [new(First, 1), new(Second, 2, Enabled: false),
            new(Choice, 3, Unused: true)], [])]);
        var cue = Assert.Single(CutsceneFlowParser.Parse(bytes)!.Cues);
        Assert.Equal(First, cue.Key);
    }

    [Fact]
    public void ConditionalTrackCanBeSkippedWithoutLosingItsUnconditionalSuccessor()
    {
        var bytes = Build([new(0, [new(First, 1), new(Second, 2, Enabled: null), new(Choice, 3)], [])]);
        var flow = CutsceneFlowParser.Parse(bytes)!;
        var first = flow.Cues.Single(cue => cue.Key == First);
        Assert.Equal(2, flow.Edges.Count(edge => edge.CurrentOffset == first.Offset));
        Assert.True(flow.Cues.Single(cue => cue.Key == Second).Conditional);
    }

    [Fact]
    public void ExactOccurrenceDistinguishesRepeatedKeysAndChoiceStopsLookahead()
    {
        var bytes = Build([new(0, [new(First, 1), new(First, 2), new(Choice, 3, Choice: true),
            new(Second, 4)], [])]);
        var manifest = Manifest(bytes);
        var repeated = manifest.Lines.Where(line => line.SourceKey == First).ToArray();
        Assert.Equal(2, repeated.Length);
        Assert.NotEqual(repeated[0].NodeId, repeated[1].NodeId);
        var observed = Assert.Single(manifest.MatchFrontier("renamed", "formatted text", [],
            rowKey: First, rowOffset: repeated[1].SourceOffset));
        Assert.Same(repeated[1], observed);
        Assert.Empty(manifest.SyntheticFuture(observed));
    }

    [Fact]
    public void NativeVoiceComesFromTheSelectedVoiceArrayEvenWithALegacyTextKey()
    {
        const string legacy = "TEXT_FIXTURE_00001_100010_ACTOR";
        var bytes = Build([new(0, [new(legacy, 1, Voices: 0b0010), new(First, 2)], [])]);
        var english = Manifest(bytes, voiceLanguage: 1);
        var japanese = Manifest(bytes, voiceLanguage: 0);
        Assert.True(english.Lines.Single(line => line.SourceKey == legacy).IsVoiced);
        Assert.Equal("ACTOR", english.Lines.Single(line => line.SourceKey == legacy).ActorToken);
        Assert.False(japanese.Lines.Single(line => line.SourceKey == legacy).IsVoiced);
        Assert.False(english.Lines.Single(line => line.SourceKey == First).IsVoiced);
    }

    [Fact]
    public void CyclicFutureEnumerationTerminatesWithoutInventingAnEnd()
    {
        var manifest = Manifest(Build([
            new(0, [new(First, 1)], [new(true, [1])]),
            new(2, [new(Second, 1)], [new(true, [0])]),
        ]));
        var current = manifest.Lines.Single(line => line.SourceKey == First);
        Assert.Equal(Second, Assert.Single(manifest.SyntheticFuture(current)).SourceKey);
        Assert.DoesNotContain(manifest.Edges!, edge => edge.NextNodeId is null);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(20)]
    public void InvalidSectionCountsAndOffsetsAreRejected(int offset)
    {
        var bytes = Build([new(0, [new(First, 1)], [])]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), Int32.MaxValue);
        Assert.Throws<InvalidDataException>(() => CutsceneFlowParser.Parse(bytes));
    }

    private static CutsceneVoiceManifest Manifest(byte[] bytes, int voiceLanguage = 1) =>
        CutsceneVoiceManifestParser.Parse(1, "scene", bytes, _ => new Dictionary<string, string>
        {
            [First] = "First dialogue", [Second] = "Second dialogue", [Choice] = "Choose",
            ["TEXT_FIXTURE_00001_100010_ACTOR"] = "Legacy native dialogue",
        }, "en", voiceLanguage);

    private sealed record Cue(string Key, short Frame, bool? Enabled = true, bool Choice = false,
        byte Voices = 0, bool Unused = false);
    private sealed record Branch(bool? Condition, int[] Targets);
    private sealed record Scene(int Kind, Cue[] Cues, Branch[] Branches, int? Id = null);

    private static byte[] Build(Scene[] scenes)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        int Here() => checked((int)stream.Position);
        void Patch(int at, int value)
        {
            var current = stream.Position; stream.Position = at; writer.Write(value); stream.Position = current;
        }
        void Rel(int field, int origin) => Patch(field, Here() - origin);
        void Text(string value) { writer.Write(Encoding.UTF8.GetBytes(value)); writer.Write((byte)0); }
        int Record(string magic, int size, short id = 0, short frame = 0)
        {
            var at = Here(); writer.Write(Encoding.ASCII.GetBytes(magic)); writer.Write(size);
            writer.Write(new byte[size - 8]); Patch(at + 8, (ushort)id | frame << 16); return at;
        }
        writer.Write("CUTB"u8); writer.Write(0); writer.Write(2 + scenes.Count(scene => scene.Kind != 3));
        var headers = new List<int>();
        foreach (var name in new[] { "CTIS", "CTCB" }.Concat(scenes.Where(scene => scene.Kind != 3).Select(_ => "CTTL")))
        {
            headers.Add(Here()); writer.Write(Encoding.ASCII.GetBytes(name)); writer.Write(16); writer.Write(0); writer.Write(0);
        }
        Rel(headers[0] + 8, headers[0] + 8);
        var sheetStart = Here(); writer.Write(8); writer.Write(0x01000001); Text(Sheet);
        Patch(headers[0] + 12, Here() - sheetStart);
        var timelineHeader = 2;
        foreach (var scene in scenes.Where(scene => scene.Kind != 3))
        {
            var header = headers[timelineHeader++]; Rel(header + 8, header + 8); var start = Here();
            writer.Write("TMLB"u8); writer.Write(0); var used = scene.Cues.Count(cue => !cue.Unused);
            writer.Write(3 + used + scene.Cues.Length);
            Record("TMDH", 16, 1); var actors = Record("TMAL", 16); var actor = Record("TMAC", 28, 2);
            var tracks = new Dictionary<int, int>(); var trackId = 3;
            for (var i = 0; i < scene.Cues.Length; i++)
                if (!scene.Cues[i].Unused) tracks[i] = Record("TMTR", 24, (short)trackId++);
            var records = new List<int>();
            for (var i = 0; i < scene.Cues.Length; i++)
            {
                var cue = scene.Cues[i]; records.Add(Record(cue.Choice ? "C098" : "C048", cue.Choice ? 40 : 64,
                    (short)(100 + i), cue.Frame));
            }
            Rel(actors + 8, actors + 8); writer.Write((short)2); Patch(actors + 12, 1);
            Rel(actor + 20, actor + 8); foreach (var track in tracks.Values) writer.Write((short)BitConverter.ToInt16(stream.GetBuffer(), track + 8));
            Patch(actor + 24, used);
            foreach (var (index, track) in tracks)
            {
                Rel(track + 12, track + 8); writer.Write((short)(100 + index)); Patch(track + 16, 1);
                var cue = scene.Cues[index];
                if (cue.Enabled != true)
                {
                    Rel(track + 20, track + 8); writer.Write(8); writer.Write(1);
                    writer.Write(cue.Enabled == false ? 21 : 19); writer.Write(0); writer.Write(0);
                }
            }
            for (var i = 0; i < scene.Cues.Length; i++)
            {
                var cue = scene.Cues[i]; var record = records[i];
                if (!cue.Choice)
                {
                    Rel(record + 36, record + 8);
                    for (var language = 0; language < 4; language++)
                    { writer.Write((cue.Voices & (1 << language)) != 0 ? 1 : 0); writer.Write(0); writer.Write(0); }
                }
                Rel(record + (cue.Choice ? 20 : 44), record + 8); Text(cue.Key);
            }
            Patch(start + 4, Here() - start); Patch(header + 12, Here() - start);
        }
        Rel(headers[1] + 8, headers[1] + 8); var table = Here(); Patch(headers[1] + 12, scenes.Length);
        writer.Write(new byte[scenes.Length * 24]);
        for (var i = 0; i < scenes.Length; i++)
        {
            var scene = scenes[i]; var record = table + i * 24;
            Patch(record + 4, scene.Kind); Patch(record + 8, scene.Id ?? i); Patch(record + 12, 65535);
            Rel(record + 16, record); Patch(record + 20, scene.Branches.Length); var branches = Here();
            writer.Write(new byte[scene.Branches.Length * 16]);
            for (var j = 0; j < scene.Branches.Length; j++)
            {
                var branch = scene.Branches[j]; var at = branches + j * 16;
                Rel(at, at); Patch(at + 4, 1); writer.Write(branch.Condition switch { true => 20, false => 21, _ => 19 });
                writer.Write(0); writer.Write(0); Rel(at + 8, at); Patch(at + 12, branch.Targets.Length);
                foreach (var next in branch.Targets) writer.Write(next);
            }
        }
        for (var i = 0; i < scenes.Length; i++) Rel(table + i * 24, table + i * 24);
        writer.Write((byte)0); Patch(4, Here());
        return stream.ToArray();
    }
}
