namespace Resonance.Audio;

public readonly record struct AudioProcessingOptions(bool CorrectionFilter, bool LineNormalization)
{
    public static AudioProcessingOptions Default => new(true, true);
}
