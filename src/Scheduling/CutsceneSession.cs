namespace Resonance.Scheduling;

public sealed record CutscenePrediction(
    string Key,
    string SpeakerKey,
    string Speaker,
    string Text,
    string Language,
    string? OfficialVoiceGroupId = null,
    ResolvedLineSpeaker? Resolution = null,
    string? SourceQuest = null);

public sealed class CutsceneSession : IDisposable
{
    private readonly Dictionary<long, DubLine> lines = [];
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private long nextSequence;
    private long speculationEpoch;
    private bool disposed;

    public long Epoch { get; }
    public uint TerritoryId { get; }
    public CancellationToken CancellationToken => lifetime.Token;
    public IReadOnlyCollection<DubLine> Lines { get { lock (gate) return lines.Values.ToArray(); } }

    public CutsceneSession(long epoch, uint territoryId)
    {
        Epoch = epoch;
        TerritoryId = territoryId;
    }

    public DubLine AddActual(string speakerKey, string speakerName, string text, string? language = null)
    {
        lock (gate)
        {
        ObjectDisposedException.ThrowIf(disposed, this);
        var sequence = ++nextSequence;
        var line = new DubLine
        {
            SessionEpoch = Epoch,
            Sequence = sequence,
            SpeakerKey = speakerKey,
            SpeakerName = speakerName,
            Text = text,
            Language = language,
            ActualStatus = ActualStatus.Actual,
            NativeVoiceStatus = NativeVoiceStatus.Unknown,
            PlaybackDeadline = DateTimeOffset.UtcNow,
        };
        line.TryTransition(DubLineState.Queued, DubLineState.Predicted);
        lines.Add(sequence, line);
        return line;
        }
    }

    public IReadOnlyList<DubLine> ReplacePredictions(
        IEnumerable<(string SpeakerKey, string Speaker, string Text)> predictions,
        string? language = null)
    {
        lock (gate)
        {
        if (disposed) return [];
        speculationEpoch++;
        var invalidated = lines.Values
            .Where(line => line.ActualStatus == ActualStatus.Predicted && !line.IsTerminal)
            .ToArray();
        foreach (var line in invalidated)
        {
            lines.Remove(line.Sequence);
            line.Cancel(DubLineState.Invalidated);
            line.Dispose();
        }

        var result = new List<DubLine>();
        var deadline = DateTimeOffset.UtcNow;
        foreach (var prediction in predictions)
        {
            deadline += TimeSpan.FromSeconds(4);
            var sequence = ++nextSequence;
            var line = new DubLine
            {
                SessionEpoch = Epoch,
                Sequence = sequence,
                SpeakerKey = prediction.SpeakerKey,
                SpeakerName = prediction.Speaker,
                Text = prediction.Text,
                Language = language,
                ActualStatus = ActualStatus.Predicted,
                NativeVoiceStatus = NativeVoiceStatus.Unknown,
                PlaybackDeadline = deadline,
            };
            lines.Add(sequence, line);
            result.Add(line);
        }
        return result;
        }
    }

    public IReadOnlyList<DubLine> ReconcilePredictions(
        IEnumerable<CutscenePrediction> predictions,
        bool preserveExisting = true)
    {
        lock (gate)
        {
        if (disposed) return [];
        speculationEpoch++;
        var desired = predictions.ToArray();
        var duplicate = desired.GroupBy(value => value.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Duplicate cutscene prediction key '{duplicate.Key}'");
        var desiredByKey = desired.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var existing = lines.Values
            .Where(line => line.ActualStatus == ActualStatus.Predicted && !line.IsTerminal)
            .ToArray();
        foreach (var line in existing)
        {
            var retain = preserveExisting
                         && line.PredictionKey is { } key
                         && desiredByKey.TryGetValue(key, out var prediction)
                         && line.SpeakerKey == prediction.SpeakerKey
                         && line.SpeakerName == prediction.Speaker
                         && line.Text == prediction.Text
                         && line.Language == prediction.Language
                         && line.OfficialVoiceGroupId == prediction.OfficialVoiceGroupId
                         && ResolutionMatches(line, prediction.Resolution);
            if (retain) continue;
            lines.Remove(line.Sequence);
            line.Cancel(DubLineState.Invalidated);
            line.Dispose();
        }

        var retainedKeys = lines.Values
            .Where(line => line.ActualStatus == ActualStatus.Predicted && !line.IsTerminal
                           && line.PredictionKey is not null)
            .Select(line => line.PredictionKey!)
            .ToHashSet(StringComparer.Ordinal);
        var added = new List<DubLine>();
        var deadline = DateTimeOffset.UtcNow;
        foreach (var prediction in desired)
        {
            deadline += TimeSpan.FromSeconds(4);
            if (retainedKeys.Contains(prediction.Key)) continue;
            var sequence = ++nextSequence;
            var line = new DubLine
            {
                SessionEpoch = Epoch,
                Sequence = sequence,
                SourceQuest = prediction.SourceQuest,
                PredictionKey = prediction.Key,
                SpeakerKey = prediction.SpeakerKey,
                SpeakerName = prediction.Speaker,
                Text = prediction.Text,
                Language = prediction.Language,
                OfficialVoiceGroupId = prediction.OfficialVoiceGroupId,
                ActualStatus = ActualStatus.Predicted,
                NativeVoiceStatus = NativeVoiceStatus.Unknown,
                PlaybackDeadline = deadline,
            };
            lines.Add(sequence, line);
            if (prediction.Resolution is { } resolution)
            {
                line.ApplyResolvedSpeaker(resolution);
                line.TransientSpeaker = resolution.SpeakerId == 0
                                        && resolution.SpeakerKey.StartsWith("scene:", StringComparison.Ordinal);
                if (line.TransientSpeaker) line.SpeakerId = null;
            }
            added.Add(line);
        }
        return added;
        }
    }

    public void ReleaseLine(long sequence)
    {
        lock (gate)
        {
            if (!lines.Remove(sequence, out var line)) return;
            line.Dispose();
        }
    }

    private static bool ResolutionMatches(DubLine line, ResolvedLineSpeaker? resolution) =>
        resolution is null
        || line.SpeakerKey == resolution.SpeakerKey
        && line.SpeakerName == resolution.SpeakerName
        && line.VoiceSex == resolution.VoiceSex
        && line.VoiceArchetype == resolution.VoiceArchetype
        && line.CastingSlotId == resolution.CastingSlotId
        && line.Casting == resolution.Casting
        && line.CastingEvidence == resolution.Evidence;

    public DubLine? PromotePrediction(
        string speakerName,
        string normalizedText,
        ResolvedLineSpeaker? resolvedSpeaker = null,
        string? language = null,
        string? predictionKey = null)
    {
        lock (gate)
        {
        if (disposed) return null;
        var line = lines.Values
            .Where(line => line.ActualStatus == ActualStatus.Predicted && !line.IsTerminal)
            .OrderBy(line => line.Sequence)
            .FirstOrDefault(line => predictionKey is not null && line.PredictionKey == predictionKey)
            ?? lines.Values
                .Where(line => line.ActualStatus == ActualStatus.Predicted && !line.IsTerminal)
                .OrderBy(line => line.Sequence)
                .FirstOrDefault(line => line.SpeakerName.Equals(speakerName, StringComparison.OrdinalIgnoreCase)
                    && line.Text == normalizedText);
        if (line is null) return null;
        var resolvedLanguage = resolvedSpeaker?.Language ?? language;
        var preservePreparedAudio = resolvedSpeaker is not null
            && String.Equals(line.Language, resolvedLanguage, StringComparison.Ordinal)
            && String.Equals(line.SpeakerKey, resolvedSpeaker.SpeakerKey, StringComparison.Ordinal)
            && (line.VoiceProfileId is not null
                || line.OfficialVoiceGroupId is not null
                || line.VoiceSex == resolvedSpeaker.VoiceSex
                && line.CastingSlotId == resolvedSpeaker.CastingSlotId
                && line.Casting == resolvedSpeaker.Casting);
        if (!preservePreparedAudio && line.State != DubLineState.Predicted)
        {
            lines.Remove(line.Sequence);
            line.Cancel(DubLineState.Invalidated);
            line.Dispose();
            return null;
        }
        line.Text = normalizedText;
        if (resolvedSpeaker is not null) line.ApplyResolvedSpeaker(resolvedSpeaker, preservePreparedAudio);
        else if (!String.IsNullOrWhiteSpace(language)) line.Language = language;
        line.ActualStatus = ActualStatus.Actual;
        line.PlaybackDeadline = DateTimeOffset.UtcNow;
        return line;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        foreach (var line in lines.Values) line.Dispose();
        lines.Clear();
        lifetime.Dispose();
        }
    }
}
