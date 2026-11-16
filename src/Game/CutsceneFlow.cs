using System.Buffers.Binary;
using System.Text;

namespace Resonance.Game;

public sealed record CutsceneCue(int Offset, string Key, int Frame, bool Conditional,
    bool IsPlayerChoice = false, byte VoiceLanguageMask = 0);
public sealed record CutsceneFlowEdge(int? CurrentOffset, int? NextOffset);
public sealed record CutsceneFlow(IReadOnlyList<CutsceneCue> Cues, IReadOnlyList<CutsceneFlowEdge> Edges);

public static class CutsceneFlowParser
{
    private sealed record Entry(string Kind, int Offset, int Size, int Time);
    private sealed record Sequence(int Kind, IReadOnlyList<int> Next, IReadOnlyList<CutsceneCue> Cues);
    private readonly record struct Section(string Kind, int Offset, int Count);

    public static IReadOnlyList<string> ReadDialogueSheetNames(ReadOnlySpan<byte> data)
    {
        var result = new List<string>();
        foreach (var section in ReadSections(data))
        {
            if (section.Kind != "CTIS" || Int(data, section.Offset) == 0) continue;
            var offset = Relative(data, section.Offset, section.Offset);
            var bytes = data[offset..];
            var end = bytes.IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException("CUTB dialogue sheet name is not terminated");
            var name = Encoding.UTF8.GetString(bytes[..end]);
            if (!String.IsNullOrWhiteSpace(name)) result.Add(name);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static CutsceneFlow? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || !data[..4].SequenceEqual("CUTB"u8)) return null;
        if (data.Length < 12) throw new InvalidDataException("CUTB header is truncated");
        var timelines = new List<int>();
        var sequenceTable = -1;
        var sequenceCount = 0;
        foreach (var section in ReadSections(data))
        {
            if (section.Kind == "CTTL") timelines.Add(section.Offset);
            else if (section.Kind == "CTCB")
            {
                sequenceTable = section.Offset;
                sequenceCount = section.Count;
                if (sequenceCount < 0 || sequenceCount > data.Length / 24)
                    throw new InvalidDataException("CUTB sequence count exceeds its data");
            }
        }
        if (sequenceTable < 0) return null;
        var sequences = new List<Sequence>();
        var timelineIndex = 0;
        for (var index = 0; index < sequenceCount; index++)
        {
            var offset = checked(sequenceTable + index * 24);
            Slice(data, offset, 24);
            var kind = Int(data, offset + 4);
            var next = ReadTransitions(data, offset);
            IReadOnlyList<CutsceneCue> cues = [];
            if (kind != 3)
            {
                if (timelineIndex == timelines.Count)
                    throw new InvalidDataException("CUTB sequence has no corresponding timeline");
                cues = ReadTimeline(data, timelines[timelineIndex++]);
            }
            sequences.Add(new(kind, next, cues));
        }
        if (timelineIndex != timelines.Count)
            throw new InvalidDataException("CUTB timeline has no corresponding sequence");
        var edges = new HashSet<CutsceneFlowEdge>();
        var all = sequences.SelectMany(sequence => sequence.Cues).ToArray();
        var pending = new Queue<(int Sequence, int Cue)>();
        void Link(int? current, int sequence, int cue)
        {
            pending.Enqueue((sequence, cue));
            var seen = new HashSet<(int, int)>();
            while (pending.TryDequeue(out var location))
            {
                if (!seen.Add(location)) continue;
                if (location.Sequence < 0 || location.Sequence >= sequences.Count)
                    throw new InvalidDataException("CUTB branch references a missing sequence ID");
                // The native CTCB loader assigns IDs from the table index, not the record's +8 field.
                var target = sequences[location.Sequence];
                var cursor = location.Cue;
                for (; cursor < target.Cues.Count; cursor++)
                {
                    var nextCue = target.Cues[cursor];
                    edges.Add(new(current, nextCue.Offset));
                    if (!nextCue.Conditional) break;
                }
                if (cursor < target.Cues.Count) continue;
                if (target.Next.Count == 0) edges.Add(new(current, null));
                foreach (var successor in target.Next) pending.Enqueue((successor, 0));
            }
        }
        for (var index = 0; index < sequences.Count; index++)
        {
            if (sequences[index].Kind == 0) Link(null, index, 0);
            for (var cue = 0; cue < sequences[index].Cues.Count; cue++)
                Link(sequences[index].Cues[cue].Offset, index, cue + 1);
        }
        return new(all, edges.ToArray());
    }

    private static IReadOnlyList<Section> ReadSections(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || !data[..4].SequenceEqual("CUTB"u8)) return [];
        if (data.Length < 12) throw new InvalidDataException("CUTB header is truncated");
        var count = Count(data, 8, 16);
        var result = new List<Section>();
        var position = 12;
        for (var index = 0; index < count; index++)
        {
            var header = Slice(data, position, 16);
            result.Add(new(Encoding.ASCII.GetString(header[..4]),
                Relative(data, position + 8, position + 8), Int(data, position + 12)));
            var size = Int(data, position + 4);
            if (size < 16 || size > data.Length - position)
                throw new InvalidDataException("CUTB section header is truncated");
            position = checked(position + size);
        }
        return result;
    }

    private static IReadOnlyList<int> ReadTransitions(ReadOnlySpan<byte> data, int sequence)
    {
        var count = Count(data, sequence + 20, 16);
        if (count == 0) return [];
        var offset = Relative(data, sequence, sequence + 16);
        Slice(data, offset, count * 16);
        var result = new List<int>();
        for (var index = 0; index < count; index++)
        {
            var branch = checked(offset + index * 16);
            Slice(data, branch, 16);
            var condition = ConstantCondition(data, branch, branch + 4);
            // The native transition routine uses the final branch as its default.
            if (condition != false || index == count - 1)
            {
                var targets = Count(data, branch + 12, 4);
                var targetOffset = Relative(data, branch, branch + 8);
                Slice(data, targetOffset, targets * 4);
                for (var target = 0; target < targets; target++)
                    result.Add(Int(data, checked(targetOffset + target * 4)));
            }
            if (condition == true) break;
        }
        return result.Distinct().ToArray();
    }

    private static IReadOnlyList<CutsceneCue> ReadTimeline(ReadOnlySpan<byte> data, int timeline)
    {
        if (!Slice(data, timeline, 4).SequenceEqual("TMLB"u8))
            throw new InvalidDataException("CUTB timeline has no TMLB header");
        var size = Int(data, timeline + 4);
        if (size < 12) throw new InvalidDataException("CUTB timeline header is truncated");
        Slice(data, timeline, size);
        var count = Count(data, timeline + 8, 8);
        var entries = new Dictionary<short, Entry>();
        var actors = Array.Empty<short>();
        var position = timeline + 12;
        for (var index = 0; index < count; index++)
        {
            var kind = Encoding.ASCII.GetString(Slice(data, position, 4));
            var entrySize = Int(data, position + 4);
            if (entrySize < 8 || entrySize > timeline + size - position)
                throw new InvalidDataException("CUTB timeline entry is truncated");
            if (kind == "TMAL") actors = ReadIds(data, position, 8);
            else if (kind is "TMAC" or "TMTR" or "C048" or "C098")
            {
                if (entrySize < 12) throw new InvalidDataException("CUTB timed entry is truncated");
                entries.Add(Short(data, position + 8), new(kind, position, entrySize, Short(data, position + 10)));
            }
            position = checked(position + entrySize);
        }
        var cues = new List<CutsceneCue>();
        foreach (var actorId in actors)
        {
            if (!entries.TryGetValue(actorId, out var actor) || actor.Kind != "TMAC")
                throw new InvalidDataException("CUTB timeline references a missing actor");
            foreach (var trackId in ReadIds(data, actor.Offset, 20))
            {
                if (!entries.TryGetValue(trackId, out var track) || track.Kind != "TMTR")
                    throw new InvalidDataException("CUTB actor references a missing track");
                var condition = Int(data, track.Offset + 20);
                bool? enabled = true;
                if (condition != 0)
                {
                    var expression = checked(track.Offset + 8 + condition);
                    enabled = ConstantCondition(data, expression, expression + 4);
                }
                if (enabled == false) continue;
                foreach (var cueId in ReadIds(data, track.Offset, 12))
                {
                    if (!entries.TryGetValue(cueId, out var cue) || cue.Kind is not ("C048" or "C098")) continue;
                    var keyField = cue.Kind == "C048" ? 44 : 20;
                    if (cue.Size < keyField + 4) throw new InvalidDataException("CUTB dialogue record is truncated");
                    if (Int(data, cue.Offset + keyField) == 0) continue;
                    var keyOffset = Relative(data, cue.Offset + 8, cue.Offset + keyField);
                    var bytes = data[keyOffset..];
                    var end = bytes.IndexOf((byte)0);
                    if (end < 0) throw new InvalidDataException("CUTB dialogue key is not terminated");
                    var key = Encoding.UTF8.GetString(bytes[..end]);
                    if (!key.StartsWith("TEXT_", StringComparison.Ordinal)) continue;
                    byte voices = 0;
                    if (cue.Kind == "C048" && Int(data, cue.Offset + 36) != 0)
                    {
                        var voiceOffset = Relative(data, cue.Offset + 8, cue.Offset + 36);
                        for (var language = 0; language < 4; language++)
                            if (Int(data, voiceOffset + language * 12) != 0) voices |= (byte)(1 << language);
                    }
                    cues.Add(new(cue.Offset, key, checked(actor.Time + track.Time + cue.Time), enabled is null,
                        cue.Kind == "C098", voices));
                }
            }
        }
        return cues.OrderBy(cue => cue.Frame).ToArray();
    }

    private static short[] ReadIds(ReadOnlySpan<byte> data, int entry, int field)
    {
        var count = Count(data, entry + field + 4, 2);
        if (count == 0) return [];
        var offset = Relative(data, entry + 8, entry + field);
        Slice(data, offset, count * 2);
        var result = new short[count];
        for (var index = 0; index < count; index++) result[index] = Short(data, offset + index * 2);
        return result;
    }

    private static bool? ConstantCondition(ReadOnlySpan<byte> data, int expression, int countField)
    {
        var count = Count(data, countField, 12);
        if (count == 0) return null;
        var offset = Relative(data, expression, expression);
        Slice(data, offset, checked(count * 12));
        return count == 1 ? Int(data, offset) switch { 20 => true, 21 => false, _ => (bool?)null } : null;
    }

    private static int Count(ReadOnlySpan<byte> data, int offset, int stride)
    {
        var value = Int(data, offset);
        if (value < 0 || value > data.Length / stride)
            throw new InvalidDataException("CUTB array count exceeds its data");
        return value;
    }

    private static int Relative(ReadOnlySpan<byte> data, int origin, int field)
    {
        var offset = (long)origin + Int(data, field);
        if (offset < 0 || offset > data.Length) throw new InvalidDataException("CUTB offset exceeds its data");
        return (int)offset;
    }

    private static int Int(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(Slice(data, offset, 4));
    private static short Short(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(Slice(data, offset, 2));
    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("CUTB offset exceeds its data");
        return data.Slice(offset, length);
    }
}
