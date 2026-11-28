using System.Numerics;
using Dalamud.Bindings.ImGui;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;

namespace Resonance.UI;

public sealed partial class ConfigWindow
{
    private Task<string?>? voicePackHash;

    private void DrawAdvanced()
    {
        Wrapped("Runtime status, measurements, and maintenance.", Muted);
        var manager = runtime();
        var boot = bootstrap();
        if (ImGui.CollapsingHeader("Runtime and audio diagnostics", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Wrapped($"Runtime: {boot.State}");
            Wrapped($"Loaded quality: {CurrentLoadedQuality}");
            Wrapped(audioBackendStatus().Diagnostic);
            if (boot.Failure is not null) Wrapped($"Bootstrap failure: {boot.Failure.Message}");
            if (manager?.Selection is { } selection)
            {
                Wrapped($"Desired: {selection.Desired.Description} [{selection.Desired.Name}]");
                Wrapped($"Effective: {selection.Effective.Description} [{selection.Effective.Name}]");
            }
            foreach (var backend in manager?.DetectedBackends ?? [])
            {
                var memory = backend.MemoryTotal == 0
                    ? "memory unknown"
                    : $"{backend.MemoryFree / 1073741824d:F1}/{backend.MemoryTotal / 1073741824d:F1} GiB free";
                Wrapped($"{backend.Type}: {backend.Description} [{backend.Name}], {memory}");
            }
            if (configuration.LegacyUnappliedOverrides.Count > 0)
                Wrapped($"Unapplied legacy overrides: {configuration.LegacyUnappliedOverrides.Count}");
            var cudaBridge = boot.CudaDriverAvailable switch
            {
                true => "available",
                false => "unavailable; Vulkan/CPU used",
                null => "probing...",
            };
            Wrapped($"CUDA driver bridge: {cudaBridge}");
            if (boot.CudaDriverAvailable == false && boot.MissingProtonCudaVariables.Count > 0)
            {
                var missing = String.Join(" and ", boot.MissingProtonCudaVariables.Select(name => $"{name}=1"));
                Wrapped($"Proton CUDA requires launching XIVLauncher with {missing}, then fully restarting XIVLauncher and FFXIV", Warning);
            }
            else if (boot.CudaDriverAvailable == false && boot.IsWineRuntime)
            {
                Wrapped("Proton variables are enabled, but this prefix has no loadable nvcuda.dll bridge; refresh it with an UMU-visible CUDA-capable Proton build", Warning);
            }
            if (boot.CudaDriverAvailable == true && !boot.CudaDeviceAvailable)
                Wrapped("The CUDA driver did not report a usable device.", Muted);
            if (boot.CudaDeviceAvailable && boot.CudaSetup is null
                && manager is not null
                && !manager.DetectedBackends.Any(candidate => candidate.Type == BackendType.Cuda))
            {
                Wrapped("CUDA backend: not installed or not loadable; the driver bridge alone is insufficient", Warning);
                if (QwenCppRuntime.CudaLoadError is { } cudaError)
                    Wrapped($"CUDA loader: {cudaError}");
            }
            if (manager?.Selection?.IsTemporaryCpuFallback == true)
            {
                Wrapped($"Temporary CPU fallback; still waiting for: {manager.Selection.Desired.Description}", Warning);
            }
        }
        var hashing = voicePackHash is { IsCompleted: false };
        ImGui.BeginDisabled(hashing);
        if (ImGui.Button(hashing ? "Hashing voice pack..." : "Hash installed voice pack"))
            Run(() => voicePackHash = Task.Run(() => hashInstalledVoicePack(shutdown.Token), shutdown.Token));
        ImGui.EndDisabled();
        if (voicePackHash is { IsCompletedSuccessfully: true })
        {
            if (voicePackHash.Result is { } hash)
            {
                Wrapped($"Voice pack SHA-256: {hash}");
                if (ImGui.SmallButton("Copy SHA-256")) ImGui.SetClipboardText(hash);
            }
            else Wrapped("No voice pack installed.", Muted);
        }
        if (ImGui.CollapsingHeader("Performance measurements and maintenance"))
        {
            var canBenchmark = manager is not null
                && boot.IsReady
                && Volatile.Read(ref benchmarkInFlight) == 0;
            ImGui.BeginDisabled(!canBenchmark);
            if (ImGui.Button(Volatile.Read(ref benchmarkInFlight) == 0
                    ? "Rebuild backend benchmark now"
                    : "Benchmarking backends..."))
                Run(() => RebuildBackendBenchmarkAsync(manager!, shutdown.Token));
            ImGui.EndDisabled();

            if (ImGui.Button("Refresh current territory voices"))
                Run(() => regenerateCurrentTerritory(shutdown.Token));

            if (configuration.BackendBenchmark is { } benchmark)
            {
                Wrapped($"Benchmark winner: {benchmark.WinnerName}");
                foreach (var measurement in benchmark.Measurements)
                {
                    var result = measurement.Successful
                        && measurement.TimeToFirstAudioSeconds is { } ttfa
                        && measurement.RealTimeFactor is { } realtime
                        ? $"TTFA {ttfa:F2}s, RTF {realtime:F2}"
                        : $"failed: {measurement.Error ?? "unknown"}";
                    Wrapped($"{measurement.BackendName}: {result}");
                }
            }
        }
        if (ImGui.CollapsingHeader("Detailed auto-advance logging"))
        {
            var diagnostics = configuration.AutoAdvanceDiagnostics;
            if (ImGui.Checkbox("Log dialogue timing and input", ref diagnostics))
            {
                configuration.AutoAdvanceDiagnostics = diagnostics;
                save();
            }
            Wrapped("Writes timing and native auto-play state to the Dalamud log. Can be noisy.");
        }
        if (ImGui.CollapsingHeader("Voice packs and storage"))
        {
            var updatePacks = !configuration.DisableVoicePackAutoUpdate;
            if (ImGui.Checkbox("Automatically update voice packs", ref updatePacks))
            {
                configuration.DisableVoicePackAutoUpdate = !updatePacks;
                save();
            }
            Wrapped("Checks for voice-pack updates on plugin startup.", Muted);
            var cacheGiB = (float)(configuration.CacheLimitBytes / 1073741824d);
            if (ImGui.SliderFloat("Saved dialogue audio", ref cacheGiB, 0f, 10f, "%.1f GiB"))
            {
                configuration.CacheLimitBytes = (long)(cacheGiB * 1073741824d);
                save();
            }
            Wrapped("Older cached audio is removed when the limit is reached.", Muted);
        }
    }

    private void DrawCastingDiagnostics()
    {
        if (!ImGui.CollapsingHeader("Casting domains")) return;
        var masculine = configuration.ReadyMasculineVoices;
        if (ImGui.SliderInt("Ready masculine voices per domain", ref masculine, 0, 20))
        {
            configuration.ReadyMasculineVoices = masculine;
            save();
        }
        var feminine = configuration.ReadyFeminineVoices;
        if (ImGui.SliderInt("Ready feminine voices per domain", ref feminine, 0, 20))
        {
            configuration.ReadyFeminineVoices = feminine;
            save();
        }
        var territory = currentTerritoryName();
        var territoryLabel = territory ?? $"unknown ({currentTerritory()})";
        Wrapped($"Territory: {territoryLabel}");
        if (territory is null || !catalog.TryGetTerritory(territory, out _))
            Wrapped("Catalog diagnostic: unknown future territory; generic_world fallback.", Warning);
        IReadOnlyList<TerritoryCastingPrior> priors = territory is null ? [] : catalog.GetNormalizedTerritoryPriors(territory);
        if (priors.Count == 0)
            ImGui.TextUnformatted("Geographic prior: none; speaker evidence or generic_world applies.");
        else
        {
            ImGui.TextUnformatted("Territory priors:");
            foreach (var prior in priors)
                Wrapped($"{prior.DomainId}: {prior.Weight:P0}");
        }
        var candidates = catalog.GetCandidateDomains(territory);
        Wrapped($"Evidence-filtered candidates (unknown speaker evidence): {String.Join(", ", candidates.Select(value => value.Id))}");

        var snapshot = poolSnapshot();
        if (snapshot is null)
        {
            ImGui.TextUnformatted("Pool: waiting for the prepared voice bank.");
            return;
        }
        Wrapped($"Active domains: {String.Join(", ", snapshot.ActiveDomains)}");
        Wrapped($"Current generation: {snapshot.CurrentGeneration ?? "idle"}");
        foreach (var domain in snapshot.ActiveDomains)
        {
            foreach (var sex in Sexes)
            {
                var key = CastingPoolScheduler.Key(domain, CurrentLanguageName(), sex);
                var ready = snapshot.ReadyCounts.GetValueOrDefault(key);
                var target = snapshot.TargetCounts.GetValueOrDefault(key);
                Wrapped($"{domain}/{sex}: {ready}/{target} ready");
            }
        }
        foreach (var failure in snapshot.Failures)
            Wrapped($"Failure: {failure}");
    }

    private void DrawDebugTabs()
    {
        if (!ImGui.BeginTabBar("##DebugSections")) return;
        if (ImGui.BeginTabItem("Listen"))
        {
            DrawVoices();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Runtime"))
        {
            DrawAdvanced();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Casting"))
        {
            DrawCastingDiagnostics();
            DrawCastingEditor();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawCastingEditor()
    {
        if (!ImGui.CollapsingHeader("Prepared domain voices")) return;
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
            }
            ImGui.EndCombo();
        }
        Wrapped("Uses the prepared voice bank for this domain.", Muted);
        if (ImGui.Button("Refresh selected domain"))
            Run(() => regenerateDomain(selectedDomain, shutdown.Token));
    }

}
