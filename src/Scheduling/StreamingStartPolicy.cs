namespace Resonance.Scheduling;

public static class StreamingStartPolicy
{
    public static bool ShouldStart(
        bool producerCompleted,
        double bufferedAudioSeconds,
        double estimatedRemainingGenerationSeconds,
        double measuredRealTimeFactor,
        double secondsSinceLineObserved)
    {
        if (bufferedAudioSeconds <= 0) return false;
        if (producerCompleted) return true;
        if (bufferedAudioSeconds >= estimatedRemainingGenerationSeconds) return true;
        return measuredRealTimeFactor <= 1.0 && secondsSinceLineObserved >= 1.0;
    }
}
