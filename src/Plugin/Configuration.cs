using Dalamud.Configuration;
using Resonance.Bootstrap;
using Resonance.Tts;

namespace Resonance.Plugin;

public enum ComputePreference
{
    AutoPerformance,
    AutoEfficiency,
    Manual,
}

public enum QualityPreset
{
    Q4Base06B,
    Q8Base06B,
    Q4Base17B,
    Q8Base17B,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;
    public bool Enabled { get; set; } = true;
    public QualityPreset Quality { get; set; } = QualityPreset.Q4Base06B;
    public ComputePreference Compute { get; set; } = ComputePreference.AutoPerformance;
    // User intent. Never rewritten by automatic error fallback.
    public string? DesiredBackendName { get; set; }
    public string? DesiredBackendDescription { get; set; }
    public BackendType? DesiredBackendType { get; set; }
    public float Volume { get; set; } = 1f;
    // Legacy v2 JSON field. Ignored: playback always follows FFXIV's mixer.
    public int AudioOutputDeviceNumber { get; set; } = -1;
    /// <summary>
    /// Keeps the Base voice-clone model resident between uses.  This does not
    /// affect the separate VoiceDesign model and defaults off for existing
    /// configuration files.
    /// </summary>
    public bool KeepBaseModelLoaded { get; set; }
    public bool BackgroundCasting { get; set; } = true;
    public bool PreDubUpcomingCutscenes { get; set; } = true;
    public bool AutoAdvanceDubbedCutsceneDialogue { get; set; }
    public bool AutoAdvanceDiagnostics { get; set; }
    public bool DisableVoicePackAutoUpdate { get; set; }
    public bool ExportDebugBaseWav { get; set; }
    public int ReadyMasculineVoices { get; set; } = 5;
    public int ReadyFeminineVoices { get; set; } = 5;
    public long CacheLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public BackendBenchmarkCache? BackendBenchmark { get; set; }

    /// <summary>Domain/language/sex prompt overrides used by catalog resolution.</summary>
    public Dictionary<string, string> PromptOverrides { get; set; } = [];

    /// <summary>
    /// Legacy v1 territory/archetype overrides are retained when they cannot be
    /// mapped to one exact catalog domain. They are diagnostic-only.
    /// </summary>
    public Dictionary<string, string> LegacyUnappliedOverrides { get; set; } = [];

    /// <summary>Compatibility deserialization surface for v1 configuration.</summary>
    [Obsolete("Migrated to PromptOverrides or LegacyUnappliedOverrides")]
    public Dictionary<string, string>? PaletteOverrides { get; set; }

    [NonSerialized] private object? promptGate = new();

    public string? GetPromptOverride(string domainId, string language, string sex)
    {
        lock (Gate)
        {
            PromptOverrides ??= [];
            return PromptOverrides.GetValueOrDefault(PromptKey(domainId, language, sex));
        }
    }

    public void SetPromptOverride(string domainId, string language, string sex, string? instruction)
    {
        lock (Gate)
        {
            PromptOverrides ??= [];
            var key = PromptKey(domainId, language, sex);
            if (string.IsNullOrWhiteSpace(instruction)) PromptOverrides.Remove(key);
            else PromptOverrides[key] = instruction.Trim();
        }
    }

    public IReadOnlyList<string> MigrateCastingV2(
        CastingProfileCatalog catalog,
        Func<uint, string?> englishTerritoryName,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(englishTerritoryName);
        var messages = new List<string>();
        lock (Gate)
        {
            PromptOverrides ??= [];
            LegacyUnappliedOverrides ??= [];
            var legacy = PaletteOverrides;
            if (legacy is not null)
            {
                foreach (var pair in legacy)
                {
                    if (String.IsNullOrWhiteSpace(pair.Value))
                    {
                        Retain(pair.Key, pair.Value ?? string.Empty, "empty legacy override");
                        continue;
                    }
                    if (!LegacyCastingMigration.TryParseKey(pair.Key, out var territoryId, out var sex))
                    {
                        Retain(pair.Key, pair.Value, "malformed legacy override");
                        continue;
                    }

                    var placeName = englishTerritoryName(territoryId);
                    IReadOnlyList<TerritoryCastingPrior> priors = placeName is null
                        ? []
                        : catalog.GetTerritoryPriors(placeName);
                    var domains = priors.Select(prior => prior.DomainId).Distinct(StringComparer.Ordinal).ToArray();
                    if (domains.Length != 1)
                    {
                        Retain(pair.Key, pair.Value, placeName is null
                            ? "English territory name unavailable"
                            : "territory has zero or multiple casting domains");
                        continue;
                    }

                    var key = PromptKey(domains[0], "english", sex);
                    if (!PromptOverrides.ContainsKey(key)) PromptOverrides[key] = pair.Value.Trim();
                    messages.Add($"Migrated legacy override '{pair.Key}' to '{key}'.");
                }
                PaletteOverrides = null;
            }

            if (Version < 2) Version = 2;
        }

        foreach (var message in messages) diagnostic?.Invoke(message);
        return messages;

        void Retain(string key, string value, string reason)
        {
            LegacyUnappliedOverrides[key] = value;
            var message = $"Retained legacy override '{key}': {reason}.";
            messages.Add(message);
        }
    }

    private object Gate => promptGate ??= new();

    private static string PromptKey(string domainId, string language, string sex) =>
        $"{domainId.Trim()}:{NormalizeLanguage(language)}:{NormalizeSex(sex)}";

    private static string NormalizeLanguage(string language) => language.Trim().ToLowerInvariant() switch
    {
        "en" or "eng" or "english" => "english",
        "ja" or "jpn" or "japanese" => "japanese",
        "de" or "deu" or "german" => "german",
        "fr" or "fra" or "french" => "french",
        _ => language.Trim().ToLowerInvariant(),
    };

    private static string NormalizeSex(string sex) => sex.Trim().ToLowerInvariant() switch
    {
        "f" or "female" or "feminine" => "feminine",
        _ => "masculine",
    };
}

[Serializable]
public sealed record BackendBenchmarkCache(
    string Identity,
    string WinnerName,
    DateTimeOffset MeasuredAt,
    IReadOnlyList<BackendBenchmarkMeasurement> Measurements);
