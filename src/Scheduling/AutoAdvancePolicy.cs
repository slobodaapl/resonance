namespace Resonance.Scheduling;

public static class AutoAdvancePolicy
{
    public static bool IsImmediateNextPredictionPlayable(IEnumerable<DubLine> lines, long completedSequence)
    {
        var next = lines
            .Where(candidate => candidate.ActualStatus == ActualStatus.Predicted
                                && candidate.Sequence > completedSequence)
            .OrderBy(candidate => candidate.Sequence)
            .FirstOrDefault();
        return next is not null && !next.IsTerminal && (next.CanStartStreaming
            || next.State == DubLineState.Buffered && next.Audio.ProducerCompleted);
    }
}
