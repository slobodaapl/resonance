using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;
using NAudio.Wave;

namespace Resonance.UI;

public sealed class ConfigWindow : Window
{
    private static readonly string[] Languages = ["english", "japanese", "german", "french"];
    private static readonly string[] Sexes = ["masculine", "feminine"];

    private readonly Configuration configuration;
    private readonly Func<RuntimeManager?> runtime;
    private readonly Action save;
    private readonly Action<Exception> reportError;
    private readonly Func<BootstrapService> bootstrap;
    private readonly Func<uint> currentTerritory;
    private readonly Func<string?> currentTerritoryName;
    private readonly Func<string> currentLanguage;
    private readonly CastingProfileCatalog catalog;
    private readonly Func<CastingPoolSnapshot?> poolSnapshot;
    private readonly Func<(bool Available, string? Reason)> nativeVoiceStatus;
    private readonly Func<CancellationToken, Task> regenerateCurrentTerritory;
    private readonly Func<string, CancellationToken, Task> regenerateDomain;
    private string selectedDomain = "generic_world";
    private string selectedLanguage = "english";
    private string selectedSex = "masculine";
    private string instruction = string.Empty;
    private string? loadedKey;

    public ConfigWindow(
        Configuration configuration,
        Func<RuntimeManager?> runtime,
        Func<BootstrapService> bootstrap,
        Func<uint> currentTerritory,
        Func<string?> currentTerritoryName,
        Func<string> currentLanguage,
        CastingProfileCatalog catalog,
        Func<CastingPoolSnapshot?> poolSnapshot,
        Func<(bool Available, string? Reason)> nativeVoiceStatus,
        Func<CancellationToken, Task> regenerateCurrentTerritory,
        Func<string, CancellationToken, Task> regenerateDomain,
        Action save,
        Action<Exception> reportError)
        : base("Resonance Settings###ResonanceSettings")
    {
        this.configuration = configuration;
        this.runtime = runtime;
        this.bootstrap = bootstrap;
        this.currentTerritory = currentTerritory;
        this.currentTerritoryName = currentTerritoryName;
        this.currentLanguage = currentLanguage;
        this.catalog = catalog;
        this.poolSnapshot = poolSnapshot;
        this.nativeVoiceStatus = nativeVoiceStatus;
        this.regenerateCurrentTerritory = regenerateCurrentTerritory;
        this.regenerateDomain = regenerateDomain;
        this.save = save;
        this.reportError = reportError;
        Size = new Vector2(560, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { configuration.Enabled = enabled; save(); }

        var quality = (int)configuration.Quality;
        var qualityNames = new[] { "Balanced — Q4", "High — Q8" };
        if (ImGui.Combo("Quality (restart required)", ref quality, qualityNames, qualityNames.Length))
        {
            configuration.Quality = (QualityPreset)quality;
            configuration.BackendBenchmark = null;
            save();
        }

        var manager = runtime();
        var boot = bootstrap();
        ImGui.TextUnformatted($"Runtime state: {boot.State}");
        if (boot.CurrentProgress is { } progress && boot.State is BootstrapState.DownloadingRuntime
                or BootstrapState.DownloadingBase or BootstrapState.DownloadingVoiceDesign)
        {
            var fraction = progress.TotalBytes <= 0 ? 0f : Math.Clamp((float)progress.BytesReceived / progress.TotalBytes, 0f, 1f);
            ImGui.ProgressBar(fraction, new Vector2(-1, 0),
                $"{progress.Id}: {progress.BytesReceived / 1048576d:F0} / {progress.TotalBytes / 1048576d:F0} MiB");
        }
        var effective = manager?.Selection?.Effective;
        ImGui.TextUnformatted($"Effective inference device: {effective?.Description ?? "Preparing..."}");
        var cudaBridge = boot.CudaDriverAvailable switch
        {
            true => "available",
            false => "unavailable; Vulkan/CPU used",
            null => "probing...",
        };
        ImGui.TextUnformatted($"CUDA driver bridge: {cudaBridge}");
        if (manager?.Selection?.IsTemporaryCpuFallback == true)
        {
            ImGui.TextColored(new Vector4(1f, .35f, .25f, 1f),
                $"Temporary CPU fallback; still waiting for: {manager.Selection.Desired.Description}");
        }

        if (manager is not null && manager.DetectedBackends.Count > 0)
        {
            var preview = configuration.Compute == ComputePreference.Manual
                ? configuration.DesiredBackendDescription ?? configuration.DesiredBackendName ?? "Manual"
                : configuration.Compute.ToString();
            if (ImGui.BeginCombo("Inference device", preview))
            {
                DrawAutomatic("Auto — Performance", ComputePreference.AutoPerformance);
                DrawAutomatic("Auto — Efficiency", ComputePreference.AutoEfficiency);
                foreach (var backend in manager.DetectedBackends)
                {
                    var selected = configuration.Compute == ComputePreference.Manual
                        && configuration.DesiredBackendName == backend.Name;
                    var label = $"{backend.Description} [{backend.Name}]";
                    if (ImGui.Selectable(label, selected))
                        _ = manager.SetDesiredAsync(backend, CancellationToken.None).ContinueWith(
                            task => reportError(task.Exception!.GetBaseException()),
                            TaskContinuationOptions.OnlyOnFaulted);
                }
                ImGui.EndCombo();
            }
        }

        var volume = configuration.Volume;
        if (ImGui.SliderFloat("Volume", ref volume, 0f, 2f, "%.2f")) { configuration.Volume = volume; save(); }
        var outputPreview = configuration.AudioOutputDeviceNumber < 0
            ? "System default"
            : GetOutputName(configuration.AudioOutputDeviceNumber);
        if (ImGui.BeginCombo("Output device (restart required)", outputPreview))
        {
            if (ImGui.Selectable("System default", configuration.AudioOutputDeviceNumber < 0))
            {
                configuration.AudioOutputDeviceNumber = -1;
                save();
            }
            for (var index = 0; index < WaveOut.DeviceCount; index++)
            {
                var name = GetOutputName(index);
                if (!ImGui.Selectable(name, configuration.AudioOutputDeviceNumber == index)) continue;
                configuration.AudioOutputDeviceNumber = index;
                save();
            }
            ImGui.EndCombo();
        }
        var casting = configuration.BackgroundCasting;
        if (ImGui.Checkbox("Background casting", ref casting)) { configuration.BackgroundCasting = casting; save(); }
        var masculine = configuration.ReadyMasculineVoices;
        if (ImGui.SliderInt("Ready masculine / active domain", ref masculine, 0, 20))
        {
            configuration.ReadyMasculineVoices = masculine;
            save();
        }
        var feminine = configuration.ReadyFeminineVoices;
        if (ImGui.SliderInt("Ready feminine / active domain", ref feminine, 0, 20))
        {
            configuration.ReadyFeminineVoices = feminine;
            save();
        }
        var cacheGiB = (float)(configuration.CacheLimitBytes / 1073741824d);
        if (ImGui.SliderFloat("Line cache limit", ref cacheGiB, 0f, 10f, "%.1f GiB"))
        {
            configuration.CacheLimitBytes = (long)(cacheGiB * 1073741824d);
            save();
        }
        if (ImGui.Button("Rebuild backend benchmark on next launch"))
        {
            configuration.BackendBenchmark = null;
            save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Regenerate current territory domains"))
            Run(regenerateCurrentTerritory(CancellationToken.None));

        DrawCastingDiagnostics();
        DrawCastingEditor();

        if (configuration.BackendBenchmark is { } benchmark)
        {
            ImGui.TextUnformatted($"Benchmark winner: {benchmark.WinnerName}");
            foreach (var measurement in benchmark.Measurements)
            {
                var result = measurement.Successful
                    ? $"TTFA {measurement.TimeToFirstAudioSeconds:F2}s, RTF {measurement.RealTimeFactor:F2}"
                    : $"failed: {measurement.Error}";
                ImGui.BulletText($"{measurement.BackendName}: {result}");
            }
        }
        ImGui.TextWrapped("Manual choices persist. Assigned NPC voices remain stable; regeneration removes only unassigned ready domain voices.");

        if (ImGui.CollapsingHeader("Diagnostics"))
        {
            var guard = nativeVoiceStatus();
            ImGui.TextUnformatted($"Native VO guard: {(guard.Available ? "available" : "unavailable")}");
            if (guard.Reason is not null) ImGui.TextWrapped($"Guard detail: {guard.Reason}");
            if (boot.Failure is not null) ImGui.TextWrapped($"Bootstrap failure: {boot.Failure.Message}");
            if (manager?.Selection is { } selection)
            {
                ImGui.TextUnformatted($"Desired: {selection.Desired.Description} [{selection.Desired.Name}]");
                ImGui.TextUnformatted($"Effective: {selection.Effective.Description} [{selection.Effective.Name}]");
            }
            foreach (var backend in manager?.DetectedBackends ?? [])
            {
                var memory = backend.MemoryTotal == 0
                    ? "memory unknown"
                    : $"{backend.MemoryFree / 1073741824d:F1}/{backend.MemoryTotal / 1073741824d:F1} GiB free";
                ImGui.BulletText($"{backend.Type}: {backend.Description} [{backend.Name}], {memory}");
            }
            if (configuration.LegacyUnappliedOverrides.Count > 0)
                ImGui.TextUnformatted($"Unapplied legacy overrides: {configuration.LegacyUnappliedOverrides.Count}");
        }

        void DrawAutomatic(string label, ComputePreference preference)
        {
            var selected = configuration.Compute == preference;
            if (!ImGui.Selectable(label, selected)) return;
            configuration.Compute = preference;
            save();
        }

        static string GetOutputName(int index)
        {
            try { return WaveOut.GetCapabilities(index).ProductName; }
            catch { return $"Output device {index}"; }
        }
    }

    private void DrawCastingDiagnostics()
    {
        if (!ImGui.CollapsingHeader("Casting domains")) return;
        var territory = currentTerritoryName();
        var territoryLabel = territory ?? $"unknown ({currentTerritory()})";
        ImGui.TextUnformatted($"Territory: {territoryLabel}");
        if (territory is null || !catalog.TryGetTerritory(territory, out _))
            ImGui.TextColored(new Vector4(1f, .75f, .25f, 1f), "Catalog diagnostic: unknown future territory; generic_world fallback.");
        IReadOnlyList<TerritoryCastingPrior> priors = territory is null ? [] : catalog.GetNormalizedTerritoryPriors(territory);
        if (priors.Count == 0)
            ImGui.TextUnformatted("Geographic prior: none; speaker evidence or generic_world applies.");
        else
        {
            ImGui.TextUnformatted("Territory priors:");
            foreach (var prior in priors)
                ImGui.BulletText($"{prior.DomainId}: {prior.Weight:P0}");
        }
        var candidates = catalog.GetCandidateDomains(territory);
        ImGui.TextUnformatted($"Evidence-filtered candidates (unknown speaker evidence): {String.Join(", ", candidates.Select(value => value.Id))}");

        var snapshot = poolSnapshot();
        if (snapshot is null)
        {
            ImGui.TextUnformatted("Pool: VoiceDesign not ready.");
            return;
        }
        ImGui.TextUnformatted($"Active domains: {String.Join(", ", snapshot.ActiveDomains)}");
        ImGui.TextUnformatted($"Current generation: {snapshot.CurrentGeneration ?? "idle"}");
        foreach (var domain in snapshot.ActiveDomains)
        {
            foreach (var sex in Sexes)
            {
                var key = CastingPoolScheduler.Key(domain, CurrentLanguageName(), sex);
                var ready = snapshot.ReadyCounts.GetValueOrDefault(key);
                var target = snapshot.TargetCounts.GetValueOrDefault(key);
                ImGui.BulletText($"{domain}/{sex}: {ready}/{target} ready");
            }
        }
        foreach (var failure in snapshot.Failures)
            ImGui.TextWrapped($"Failure: {failure}");
    }

    private void DrawCastingEditor()
    {
        if (!ImGui.CollapsingHeader("Domain prompt editor")) return;
        var domains = catalog.Domains.Select(domain => domain.Id).ToArray();
        var domainIndex = Array.IndexOf(domains, selectedDomain);
        if (domainIndex < 0) domainIndex = 0;
        if (ImGui.BeginCombo("Domain", domains[domainIndex]))
        {
            for (var index = 0; index < domains.Length; index++)
            {
                if (!ImGui.Selectable(domains[index], index == domainIndex)) continue;
                domainIndex = index;
                selectedDomain = domains[index];
                loadedKey = null;
            }
            ImGui.EndCombo();
        }
        var languageIndex = Array.IndexOf(Languages, selectedLanguage);
        if (languageIndex < 0) languageIndex = 0;
        if (ImGui.Combo("Language", ref languageIndex, Languages, Languages.Length))
        {
            selectedLanguage = Languages[languageIndex];
            loadedKey = null;
        }
        var sexIndex = Array.IndexOf(Sexes, selectedSex);
        if (sexIndex < 0) sexIndex = 0;
        if (ImGui.Combo("Sex", ref sexIndex, Sexes, Sexes.Length))
        {
            selectedSex = Sexes[sexIndex];
            loadedKey = null;
        }
        var key = CastingPoolScheduler.Key(selectedDomain, selectedLanguage, selectedSex);
        if (loadedKey != key)
        {
            var place = currentTerritoryName();
            var evidence = new SpeakerCastingEvidence("settings", place, place, Sex: selectedSex);
            var resolution = catalog.Resolve(evidence) with
            {
                DomainId = selectedDomain,
                ModifierIds = [],
                CandidateDomainIds = [selectedDomain],
            };
            instruction = configuration.GetPromptOverride(selectedDomain, selectedLanguage, selectedSex)
                ?? catalog.BuildPrompt(resolution, selectedLanguage, selectedSex);
            loadedKey = key;
        }
        ImGui.InputTextMultiline("##DomainInstruction", ref instruction, 4096, new Vector2(-1, 120));
        if (ImGui.Button("Save domain prompt override"))
        {
            configuration.SetPromptOverride(selectedDomain, selectedLanguage, selectedSex, instruction);
            save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Reset domain prompt override"))
        {
            configuration.SetPromptOverride(selectedDomain, selectedLanguage, selectedSex, null);
            loadedKey = null;
            save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Regenerate selected domain"))
            Run(regenerateDomain(selectedDomain, CancellationToken.None));
    }

    private string CurrentLanguageName()
    {
        var value = currentLanguage().Trim().ToLowerInvariant();
        return Languages.Contains(value, StringComparer.Ordinal) ? value : "english";
    }

    private void Run(Task task) => _ = task.ContinueWith(
        completed => reportError(completed.Exception!.GetBaseException()),
        TaskContinuationOptions.OnlyOnFaulted);
}
