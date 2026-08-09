namespace Resonance.Game;

public sealed class ScdExtractor
{
    public Task<float[]> ExtractMono24KhzAsync(string path, uint soundNumber, CancellationToken token) =>
        throw new NotSupportedException("The official-reference tests add PCM directly");
}
