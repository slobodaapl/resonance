namespace Resonance.Game;

public sealed class ScdExtractor
{
    public Task<float[]> ExtractMono24KhzAsync(string path, uint soundNumber, CancellationToken token) =>
        throw new NotSupportedException("The official-reference tests add PCM directly");

    public Task<byte[]> CaptureResourceBytesAsync(
        string path,
        CancellationToken token,
        bool logFailure = true) =>
        throw new NotSupportedException("Game resources are unavailable in managed tests");
}
