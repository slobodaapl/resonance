using System.Text.Json;

namespace Resonance.Bootstrap;

internal static class BenchmarkVoice
{
    internal static BaseHostReferencePayload Load(string modelPath)
    {
        using var input = typeof(BenchmarkVoice).Assembly.GetManifestResourceStream("Resonance.BenchmarkVoices.json")
            ?? throw new InvalidDataException("Benchmark voice bank is missing");
        var references = JsonSerializer.Deserialize<Dictionary<string, BaseHostReferencePayload>>(input)
            ?? throw new InvalidDataException("Benchmark voice bank is invalid");
        if (!references.TryGetValue(Path.GetFileName(modelPath), out var reference))
            throw new InvalidDataException($"No benchmark voice for model '{Path.GetFileName(modelPath)}'");
        BaseHostProtocol.ValidateVoiceReferencePayload(reference);
        return reference;
    }
}
