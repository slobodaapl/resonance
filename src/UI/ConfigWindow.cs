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
    private readonly Func<string, DebugInferenceSnapshot> debugSnapshot;
    private readonly Func<string, CancellationToken, Task> refreshDebugBaseVoices;
    private readonly Func<string, string, string, CancellationToken, Task> runVoiceDesignDebug;
    private readonly Func<string, string, string, CancellationToken, Task> runBaseDebug;
    private readonly Action cancelDebug;
    private string selectedDomain = "generic_world";
    private string selectedLanguage = "english";
    private string selectedSex = "masculine";
    private string instruction = string.Empty;
    private string? loadedKey;
    private string debugLanguage = "english";
    private string debugSentence = "The light of the crystal guides us through the dark.";
    private string debugInstruction = "A clear, natural adult voice with measured pacing and restrained emotion.";
    private string debugBaseVoice = "alphinaud";
    private string? refreshedDebugLanguage;
    private int debugRefreshInFlight;
    private int benchmarkInFlight;

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
        Func<string, DebugInferenceSnapshot> debugSnapshot,
        Func<string, CancellationToken, Task> refreshDebugBaseVoices,
        Func<string, string, string, CancellationToken, Task> runVoiceDesignDebug,
        Func<string, string, string, CancellationToken, Task> runBaseDebug,
        Action cancelDebug,
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
        this.debugSnapshot = debugSnapshot;
        this.refreshDebugBaseVoices = refreshDebugBaseVoices;
        this.runVoiceDesignDebug = runVoiceDesignDebug;
        this.runBaseDebug = runBaseDebug;
        this.cancelDebug = cancelDebug;
        this.save = save;
        this.reportError = reportError;
        debugLanguage = CurrentLanguageName();
        Size = new Vector2(560, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##ResonanceSettingsTabs")) return;
        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettings();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Debug"))
        {
            DrawDebug();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawSettings()
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
        if (boot.CudaDriverAvailable == true
            && manager is not null
            && !manager.DetectedBackends.Any(candidate => candidate.Type == BackendType.Cuda))
        {
            ImGui.TextColored(new Vector4(1f, .75f, .25f, 1f),
                "CUDA backend: not installed or not loadable; the driver bridge alone is insufficient");
        }
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
        var autoAdvance = configuration.AutoAdvanceDubbedCutsceneDialogue;
        if (ImGui.Checkbox("Auto-advance prepared dubbed cutscene dialogue", ref autoAdvance))
        {
            configuration.AutoAdvanceDubbedCutsceneDialogue = autoAdvance;
            save();
        }
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
        var canBenchmark = manager is not null
            && boot.VoiceDesignPath is not null
            && configuration.Compute != ComputePreference.Manual
            && Volatile.Read(ref benchmarkInFlight) == 0;
        ImGui.BeginDisabled(!canBenchmark);
        if (ImGui.Button(Volatile.Read(ref benchmarkInFlight) == 0
                ? "Rebuild backend benchmark now"
                : "Benchmarking backends..."))
            Run(RebuildBackendBenchmarkAsync(manager!, boot.VoiceDesignPath!));
        ImGui.EndDisabled();

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

    private void DrawDebug()
    {
        var manager = runtime();
        var snapshot = debugSnapshot(debugLanguage);
        ImGui.TextUnformatted("Inference smoke tests");
        ImGui.TextWrapped("Runs real Base and VoiceDesign inference on the selected device and streams the result through Resonance's in-game audio output.");
        ImGui.Separator();
        ImGui.TextUnformatted($"Readiness: {snapshot.Readiness}");
        ImGui.TextUnformatted($"Device: {snapshot.Device}");
        ImGui.TextWrapped($"Status: {snapshot.Status}");

        if (snapshot.Ready && Volatile.Read(ref refreshedDebugLanguage) != debugLanguage
                           && Volatile.Read(ref debugRefreshInFlight) == 0)
            RefreshDebugVoices();

        var controlsDisabled = !snapshot.Ready || snapshot.Running;
        ImGui.BeginDisabled(controlsDisabled);
        if (manager is not null && manager.DetectedBackends.Count > 0)
        {
            var preview = manager.Selection?.Effective.Description ?? "Preparing...";
            if (ImGui.BeginCombo("Test inference device", preview))
            {
                foreach (var backend in manager.DetectedBackends)
                {
                    var selected = manager.Selection?.Effective.Name == backend.Name;
                    if (ImGui.Selectable($"{backend.Description} [{backend.Name}]", selected))
                        Run(manager.SetDesiredAsync(backend, CancellationToken.None));
                }
                ImGui.EndCombo();
            }
        }

        var languageIndex = Array.IndexOf(Languages, debugLanguage);
        if (languageIndex < 0) languageIndex = 0;
        if (ImGui.Combo("Test language", ref languageIndex, Languages, Languages.Length))
        {
            debugLanguage = Languages[languageIndex];
            Volatile.Write(ref refreshedDebugLanguage, null);
        }
        ImGui.TextUnformatted("Sample sentence");
        ImGui.InputTextMultiline("##DebugSampleSentence", ref debugSentence, 2048, new Vector2(-1, 90));
        ImGui.TextUnformatted("VoiceDesign instruction");
        ImGui.InputTextMultiline("##DebugVoiceDesignInstruction", ref debugInstruction, 4096, new Vector2(-1, 90));
        if (ImGui.Button("Test VoiceDesign + playback"))
            Run(runVoiceDesignDebug(debugSentence, debugInstruction, debugLanguage, CancellationToken.None));

        ImGui.Separator();
        ImGui.TextUnformatted("Base voice clone");
        ImGui.TextWrapped("Uses verified official game resources. Curated sources build lazily; captured sources persist for later reuse.");
        var voices = snapshot.BaseVoices;
        var selectedVoice = voices.FirstOrDefault(value => value.Key == debugBaseVoice) ?? voices.First();
        if (ImGui.BeginCombo("Official voice", $"{selectedVoice.Label} — {selectedVoice.SourceStatus}"))
        {
            foreach (var voice in voices)
            {
                var label = $"{voice.Label} — {voice.SourceStatus}";
                if (!ImGui.Selectable(label, voice.Key == debugBaseVoice)) continue;
                debugBaseVoice = voice.Key;
            }
            ImGui.EndCombo();
        }
        ImGui.BeginDisabled(!selectedVoice.Available);
        if (ImGui.Button("Test Base clone + playback"))
            Run(runBaseDebug(debugBaseVoice, debugSentence, debugLanguage, CancellationToken.None));
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Refresh official sources")) RefreshDebugVoices();
        ImGui.EndDisabled();

        if (snapshot.Running && ImGui.Button("Stop debug playback")) cancelDebug();
    }

    private void RefreshDebugVoices()
    {
        if (Interlocked.Exchange(ref debugRefreshInFlight, 1) != 0) return;
        var language = debugLanguage;
        _ = refreshDebugBaseVoices(language, CancellationToken.None).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) Volatile.Write(ref refreshedDebugLanguage, language);
            else if (task.IsFaulted) reportError(task.Exception!.GetBaseException());
            Interlocked.Exchange(ref debugRefreshInFlight, 0);
        }, TaskScheduler.Default);
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

    private async Task RebuildBackendBenchmarkAsync(RuntimeManager manager, string voiceDesignPath)
    {
        if (Interlocked.Exchange(ref benchmarkInFlight, 1) != 0) return;
        try
        {
            configuration.BackendBenchmark = null;
            save();
            await manager.BenchmarkAndApplyAsync(voiceDesignPath, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref benchmarkInFlight, 0);
        }
    }

    private void Run(Task task) => _ = task.ContinueWith(
        completed => reportError(completed.Exception!.GetBaseException()),
        TaskContinuationOptions.OnlyOnFaulted);
}
