namespace Resonance.Tts;

public static class VoiceEmbedding
{
    public static float[] LinearBlend(ReadOnlySpan<float> foundation, ReadOnlySpan<float> flavor, double weight)
    {
        if (foundation.IsEmpty || foundation.Length != flavor.Length)
            throw new ArgumentException("Voice embedding dimensions differ or are empty");
        if (!double.IsFinite(weight) || weight is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(weight));
        var result = new float[foundation.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if (!float.IsFinite(foundation[i]) || !float.IsFinite(flavor[i]))
                throw new ArgumentException("Voice embedding contains a non-finite value");
            result[i] = (float)((1 - weight) * foundation[i] + weight * flavor[i]);
        }
        return result;
    }
}
