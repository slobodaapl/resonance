using System.Collections.Concurrent;
using System.Text.Json;

namespace Resonance.Tts;

internal static class FallbackVoiceContinuity
{
    internal static StoredVoiceProfile Remember(ConcurrentDictionary<string, StoredVoiceProfile> cache,
        string key, StoredVoiceProfile candidate) =>
        candidate.Kind == VoiceProfileKind.Designed && !IsExplicitVoice(candidate.SourceMetadata)
            ? cache.GetOrAdd(key, candidate)
            : candidate;

    private static bool IsExplicitVoice(string? metadata)
    {
        if (String.IsNullOrWhiteSpace(metadata)) return false;
        try
        {
            using var json = JsonDocument.Parse(metadata);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("promptOverride", out var value)
                && (value.ValueKind == JsonValueKind.True
                    || value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var flag) && flag == 1);
        }
        catch (JsonException) { return true; }
    }
}
