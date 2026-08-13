namespace Resonance.Scheduling;

public enum AutoAdvanceSuccessorState { Ready, Waiting, Unavailable }

public static class AutoAdvancePolicy
{
    public static AutoAdvanceSuccessorState GetSuccessorState(
        IEnumerable<DubLine> lines, string? predictionKey,
        bool requirePreparedPlaybackAsset = false)
    {
        if (predictionKey is null) return AutoAdvanceSuccessorState.Ready;
        var next = lines.FirstOrDefault(candidate =>
            candidate.ActualStatus == ActualStatus.Predicted
            && candidate.PredictionKey == predictionKey);
        if (next is null) return AutoAdvanceSuccessorState.Waiting;
        if (next.IsTerminal) return AutoAdvanceSuccessorState.Unavailable;
        if (requirePreparedPlaybackAsset)
            return next.PlaybackAssetReady
                ? AutoAdvanceSuccessorState.Ready
                : AutoAdvanceSuccessorState.Waiting;
        return next.CanStartStreaming
               || next.State == DubLineState.Buffered && next.Audio.ProducerCompleted
            ? AutoAdvanceSuccessorState.Ready
            : AutoAdvanceSuccessorState.Waiting;
    }

    public static AutoAdvanceSuccessorState GetSuccessorSetState(
        IEnumerable<DubLine> lines, IReadOnlyCollection<string> predictionKeys,
        bool requirePreparedPlaybackAsset = false)
    {
        if (predictionKeys.Count == 0) return AutoAdvanceSuccessorState.Ready;
        var all = lines.ToArray();
        var states = predictionKeys.Select(key => GetSuccessorState(
            all, key, requirePreparedPlaybackAsset)).ToArray();
        if (states.Any(state => state == AutoAdvanceSuccessorState.Waiting))
            return AutoAdvanceSuccessorState.Waiting;
        return states.Any(state => state == AutoAdvanceSuccessorState.Unavailable)
            ? AutoAdvanceSuccessorState.Unavailable
            : AutoAdvanceSuccessorState.Ready;
    }

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
