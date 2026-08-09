using Dalamud.Plugin.Services;

namespace Resonance.Game;

public sealed class ScdExtractor(IDataManager dataManager)
{
    public Task<float[]> ExtractMono24KhzAsync(string path, uint soundNumber, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var resource = dataManager.GetFile(path) ?? throw new FileNotFoundException("SCD resource is unavailable", path);
        return ScdAudioDecoder.Extract(resource.Data, soundNumber, token);
    }, token);
}
