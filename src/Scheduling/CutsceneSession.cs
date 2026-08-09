namespace Resonance.Scheduling;

public sealed class CutsceneSession : IDisposable
{
    private readonly Dictionary<long, DubLine> lines = [];
    private readonly object gate = new();
    private long nextSequence;
    private long speculationEpoch;
    private bool disposed;

    public long Epoch { get; }
    public uint TerritoryId { get; }
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
            State = DubLineState.Queued,
            PlaybackDeadline = DateTimeOffset.UtcNow,
        };
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
                State = DubLineState.Predicted,
                PlaybackDeadline = deadline,
            };
            lines.Add(sequence, line);
            result.Add(line);
        }
        return result;
        }
    }

    public DubLine? PromotePrediction(
        string speakerName,
        string normalizedText,
        ResolvedLineSpeaker? resolvedSpeaker = null,
        string? language = null)
    {
        lock (gate)
        {
        if (disposed) return null;
        var line = lines.Values
            .Where(line => line.ActualStatus == ActualStatus.Predicted && !line.IsTerminal)
            .OrderBy(line => line.Sequence)
            .FirstOrDefault(line => line.SpeakerName.Equals(speakerName, StringComparison.OrdinalIgnoreCase)
                && line.Text == normalizedText);
        if (line is null) return null;
        var resolvedLanguage = resolvedSpeaker?.Language ?? language;
        var preservePreparedAudio = resolvedSpeaker is not null
            && line.VoiceProfileId is not null
            && line.SpeakerKey == resolvedSpeaker.SpeakerKey
            && String.Equals(line.Language, resolvedLanguage, StringComparison.Ordinal);
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
        foreach (var line in lines.Values) line.Dispose();
        lines.Clear();
        }
    }
}
