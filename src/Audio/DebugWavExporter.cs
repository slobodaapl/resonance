using NAudio.Wave;

namespace Resonance.Audio;

internal static class DebugWavExporter
{
    public static void ExportRaw(string path, ReadOnlySpan<float> samples)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        WriteFloat(path, samples.ToArray(), 24_000, 1);
    }

    public static void Export(
        string path,
        ReadOnlySpan<float> samples,
        float volume)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var prepared = GameMixerPcmEncoder.PrepareMono44100(samples, true, Math.Clamp(volume, 0f, 2f));
        WriteFloat(path, prepared.Samples, prepared.SampleRate, 1);
    }

    private static void WriteFloat(string path, float[] samples, int sampleRate, int channels)
    {
        using var writer = new WaveFileWriter(
            path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
