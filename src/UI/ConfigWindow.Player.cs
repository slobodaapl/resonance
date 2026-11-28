using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Resonance.Bootstrap;
using Resonance.Plugin;
using Resonance.Tts;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;

namespace Resonance.UI;

public sealed partial class ConfigWindow
{
    private static readonly Vector4 Accent = new(.90f, .77f, .49f, 1f);
    private static readonly Vector4 Text = new(.94f, .95f, .97f, 1f);
    private static readonly Vector4 Muted = new(.74f, .78f, .84f, 1f);
    private static readonly Vector4 Good = new(.48f, .84f, .69f, 1f);
    private static readonly Vector4 Warm = new(1f, .57f, .25f, 1f);
    private static readonly Vector4 Warning = Warm;
    private static readonly Vector4 Error = new(.96f, .32f, .36f, 1f);
    private static readonly Vector4 Ink = new(.12f, .13f, .16f, 1f);
    private static readonly string[] LanguageLabels = ["English", "Japanese", "German", "French"];
    private static readonly string[] QualityLabels = ["Fast", "Balanced", "High", "Highest"];
    private int page;
    private bool resetPageScroll;
    private double pageChangedAt = double.NegativeInfinity;
    private QualityPreset? hoveredQuality;
    private string? frameError;
    private bool refreshMemoryOnOpen;
    private int pendingQuality = -1;
    private QualityPreset SelectedQuality => Volatile.Read(ref pendingQuality) is var value && value >= 0
        ? (QualityPreset)value : configuration.Quality;
    private string? refreshedVoiceModel;
    private string voiceLanguage = "english";
    private string voiceSearch = string.Empty;
    private string previewVoice = "alphinaud";
    private string previewSentence = SampleSentence;
    private bool showUnavailableVoices;
    private string? refreshedVoiceLanguage;
    private int voiceRefreshInFlight;
    private bool voiceRefreshFailed;
    private bool previewPlayed;
    private static float Scale => ImGuiHelpers.GlobalScale;

    public override void OnOpen() => refreshMemoryOnOpen = true;

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8) * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8, 4) * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemInnerSpacing, new Vector2(8, 4) * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12f * Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 12f * Scale);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(.060f, .070f, .090f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(.105f, .120f, .148f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Muted);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(.23f, .26f, .31f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(.17f, .19f, .23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(.24f, .27f, .32f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(.30f, .28f, .23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.18f, .21f, .26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.28f, .31f, .36f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.35f, .32f, .25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(.29f, .27f, .22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(.27f, .29f, .33f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(.35f, .31f, .24f, 1f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, new Vector4(.98f, .88f, .64f, 1f));
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, Accent);
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(.15f, .17f, .21f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(.29f, .32f, .37f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(.29f, .27f, .22f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(.075f, .085f, .105f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(.105f, .120f, .148f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, new Vector4(.075f, .085f, .105f, 1f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(24);
        ImGui.PopStyleVar(7);
    }

    public override void Draw()
    {
        frameActivity = playbackActivity();
        hoveredQuality = null;
        var boot = bootstrap();
        if (refreshMemoryOnOpen && boot.IsReady && debugSnapshot(CurrentLanguageName()).Ready)
        {
            refreshMemoryOnOpen = false;
            Run(() => refreshDeviceMemory(shutdown.Token));
        }
        var output = audioBackendStatus();
        frameError = Volatile.Read(ref operationError) ?? boot.Failure?.Message
            ?? (boot.State == BootstrapState.Failed ? "The speech engine failed to start."
                : boot.IsReady && (!output.Available || !output.Healthy) ? output.Diagnostic : null);
        EnsureVoiceList(CurrentLanguageName());
        var titleHeight = ImGui.GetFrameHeight();
        using var bodyPadding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(12, 8) * Scale);
        using var panelPadding = ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(16) * Scale);
        using var bodyInnerSpacing = ImRaii.PushStyle(ImGuiStyleVar.ItemInnerSpacing, new Vector2(4) * Scale);
        var p = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var draw = ImGui.GetWindowDrawList();
        var top = p + new Vector2(1, titleHeight);
        draw.AddRectFilledMultiColor(top, p + size - Vector2.One,
            Color(new Vector4(.095f, .108f, .135f, 1)), Color(new Vector4(.095f, .108f, .135f, 1)),
            Color(new Vector4(.060f, .070f, .090f, 1)), Color(new Vector4(.060f, .070f, .090f, 1)));
        draw.AddLine(top, new Vector2(p.X + size.X, top.Y), Color(Accent with { W = .55f }), Scale);
        DrawTitle();
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(0, 16) * Scale))
        using (ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, Vector2.Zero))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 0))
        {
            if (BeginPanel("##Navigation", new Vector2(184 * Scale, 0))) DrawNavigation();
            EndPanel("##Navigation");
        }
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        if (ImGui.BeginChild("##Page", new Vector2(-ImGui.GetStyle().ItemSpacing.X, 0), false))
        {
            if (ImGui.BeginChild("##Content", Vector2.Zero, false))
            {
                if (resetPageScroll)
                {
                    ImGui.SetScrollY(0);
                    resetPageScroll = false;
                }
                var opacity = configuration.ReduceMotion ? 1 : Math.Clamp((float)((ImGui.GetTime() - pageChangedAt) / .15), 0, 1);
                using var fade = ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * opacity);
                if (frameError is { } error)
                {
                    if (BeginPanel("##Error", Vector2.Zero, true))
                    {
                        ImGui.TextColored(Error, "Error");
                        Wrapped(error, Error);
                        if (Volatile.Read(ref operationError) is not null
                            && ImGui.SmallButton(Volatile.Read(ref voiceRefreshFailed) ? "Retry" : "Dismiss"))
                        {
                            Volatile.Write(ref operationError, null);
                            if (Volatile.Read(ref voiceRefreshFailed))
                            {
                                Volatile.Write(ref voiceRefreshFailed, false);
                                refreshedVoiceLanguage = null;
                            }
                        }
                    }
                    EndPanel("##Error");
                }
                DrawSetupFeedback();
                if (page == 0) DrawModelPage();
                else if (page == 1) DrawPlaybackPage();
                else if (page == 2) DrawAccessibilityPage();
                else
                {
                    if (BeginPanel("##Debug", Vector2.Zero))
                    {
                        Heading("Debug");
                        Caption($"Resonance v{typeof(ConfigWindow).Assembly.GetName().Version}");
                        DrawDebugTabs();
                    }
                    EndPanel("##Debug");
                }
            }
            ImGui.EndChild();
        }
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    private void SelectPage(int next)
    {
        if (page == next) return;
        page = next;
        resetPageScroll = true;
        pageChangedAt = ImGui.GetTime();
    }

    private (string Text, Vector4 Color) Status()
    {
        if (frameError is { } error) return ($"Error: {error.Split('\n')[0]}", Error);
        if (bootstrap().IsChangingQuality) return ("Loading model...", Warning);
        if (!configuration.Enabled || frameActivity.Paused) return ("Paused", Muted);
        if (!bootstrap().IsReady || debugSnapshot(CurrentLanguageName()).Running && runtime()?.BaseModelLoaded != true)
            return ("Loading model...", Warning);
        if (frameActivity.Speaking) return ("Speaking", Good);
        if (frameActivity.Generating) return ("Generating...", Warning);
        return ("Ready", Good);
    }

    private void DrawNavigation()
    {
        if (NavigationRow("Model", FontAwesomeIcon.Microchip, 0)) SelectPage(0);
        if (NavigationRow("Playback", FontAwesomeIcon.Play, 1)) SelectPage(1);
        if (NavigationRow("Accessibility", FontAwesomeIcon.UniversalAccess, 2)) SelectPage(2);
        if (NavigationRow("Debug", FontAwesomeIcon.Bug, 3)) SelectPage(3);
        var status = Status();
        var label = status.Text.Length > 100 ? status.Text[..97] + "..." : status.Text;
        var labelWidth = ImGui.GetContentRegionAvail().X - 54 * Scale;
        var labelHeight = ImGui.CalcTextSize(label, false, labelWidth).Y;
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), ImGui.GetWindowHeight() - labelHeight - 16 * Scale));
        var p = ImGui.GetCursorScreenPos() + new Vector2(16 * Scale, 0);
        var size = new Vector2(ImGui.GetContentRegionAvail().X, labelHeight);
        var pulse = configuration.ReduceMotion ? 1f : frameActivity.Speaking
            ? .45f + .55f * SignalLevel(frameActivity.OutputPeak ?? 0)
            : .8f + .2f * MathF.Sin((float)ImGui.GetTime() * (status.Color == Warning ? 3 : .5f));
        var center = p + new Vector2(6, 9) * Scale;
        var draw = ImGui.GetWindowDrawList();
        for (var layer = 3; layer >= 1; layer--)
            draw.AddCircleFilled(center, (2 + layer * 2) * Scale, Color(status.Color with { W = .025f * pulse }));
        draw.AddCircleFilled(center, 3 * Scale, Color(status.Color with { W = pulse }));
        TextAt(p + new Vector2(22 * Scale, 0), label, status.Color, labelWidth);
        ImGui.Dummy(new Vector2(size.X, labelHeight));
        if (ImGui.IsItemHovered())
        {
            var device = runtime()?.Selection?.Effective;
            var timing = configuration.BackendBenchmark?.Measurements.FirstOrDefault(value => value.BackendName == device?.Name && value.Successful);
            ImGui.SetTooltip(frameError ?? $"{device?.Description} / {device?.Type}"
                + (timing?.TimeToFirstAudioSeconds is { } first ? $"\nBenchmark: {first:F2}s to first audio" : string.Empty));
        }
    }

    private void DrawTitle()
    {
        using var corners = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 0);
        if (BeginPanel("##Title", new Vector2(0, 72 * Scale), border: false))
        {
            var p = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            float titleWidth;
            using (headingFont.Push())
            {
                titleWidth = ImGui.CalcTextSize("Resonance").X * 1.25f;
                TextAt(p + new Vector2(0, 7) * Scale, "Resonance", Accent, fontScale: 1.25f);
            }
            var wave = p + new Vector2(titleWidth + 24 * Scale, 0);
            var waveWidth = p.X + width - wave.X;
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(wave, wave + new Vector2(waveWidth, 40 * Scale), Color(new Vector4(.065f, .075f, .095f, 1)), 8 * Scale);
            DrawSignal(wave + new Vector2(8, 4) * Scale, new Vector2(waveWidth - 16 * Scale, 32 * Scale), frameActivity.OutputPeak);
            var bottom = ImGui.GetWindowPos() + new Vector2(0, ImGui.GetWindowHeight() - Scale);
            draw.AddLine(bottom, bottom + new Vector2(ImGui.GetWindowWidth(), 0),
                Color(new Vector4(.23f, .26f, .31f, 1)), Scale);
        }
        EndPanel("##Title");
    }

    private void DrawPreviewControl()
    {
        var snapshot = debugSnapshot(CurrentLanguageName());
        var ready = snapshot.Ready && snapshot.BaseVoices.Any(value => value.Available)
            && configuration.Quality == CurrentLoadedQuality;
        var p = ImGui.GetCursorScreenPos();
        ImGui.BeginDisabled(bootstrap().IsChangingQuality || !snapshot.Running && !ready);
        var pressed = ClickArea("##VoicePreview", new Vector2(176, 40) * Scale);
        var draw = ImGui.GetWindowDrawList();
        RoundedGradient(p, new Vector2(176, 40) * Scale, Accent, Warm, 8 * Scale);
        if (bootstrap().IsChangingQuality || snapshot.Running && !frameActivity.Speaking)
        {
            var phase = configuration.ReduceMotion ? 0 : (float)ImGui.GetTime() * 5;
            for (var index = 0; index < 10; index++)
            {
                var angle = phase + index * .4f;
                draw.AddLine(p + (new Vector2(20) + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 8) * Scale,
                    p + (new Vector2(20) + new Vector2(MathF.Cos(angle + .3f), MathF.Sin(angle + .3f)) * 8) * Scale,
                    Color(Ink with { W = .2f + index * .08f }), 2 * Scale);
            }
        }
        else IconAt(p + new Vector2(12, 12) * Scale, snapshot.Running ? FontAwesomeIcon.Stop : FontAwesomeIcon.Play, Ink);
        TextAt(p + new Vector2(40, 11) * Scale, bootstrap().IsChangingQuality ? "Switching..."
            : snapshot.Running ? "Stop preview" : "Test voice", Ink);
        ImGui.EndDisabled();
        if (pressed)
        {
            if (snapshot.Running) cancelDebug();
            else
            {
                var voice = snapshot.BaseVoices.First(value => value.Available);
                Run(() => runBaseDebug(voice.Key, SampleSentence, CurrentLanguageName(), shutdown.Token));
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(snapshot.Running ? "Stop voice preview" : configuration.Quality != CurrentLoadedQuality
                ? "Applying the selected quality before previewing it."
                : ready ? "Test an installed voice" : !snapshot.VoicesCached ? "Preparing character voices..."
                : !snapshot.BaseVoices.Any(value => value.Available) ? "No installed voice profiles for the active model." : snapshot.Readiness);
    }

    private QualityPreset CurrentLoadedQuality
    {
        get
        {
            var hash = runtime()?.ModelHash;
            if (hash is not null)
                foreach (var quality in Enum.GetValues<QualityPreset>())
                    if (bootstrap().ModelAssets.FirstOrDefault(value => value.Id == ModelQualitySelection.Resolve(quality).Base)
                        ?.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase) == true) return quality;
            return loadedQuality;
        }
    }

    private void DrawVoices()
    {
        Wrapped("Listen to an installed character voice through the game audio output.", Muted);
        var languageIndex = Math.Max(0, Array.IndexOf(Languages, voiceLanguage));
        ImGui.SetNextItemWidth(160 * Scale);
        if (ImGui.Combo("Language", ref languageIndex, LanguageLabels, LanguageLabels.Length))
        {
            voiceLanguage = Languages[languageIndex];
            previewPlayed = false;
        }
        EnsureVoiceList(voiceLanguage);
        var snapshot = debugSnapshot(voiceLanguage);
        Wrapped($"{snapshot.BaseVoices.Count(voice => voice.Available)} available for the loaded voice quality.", Muted);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##VoiceSearch", "Search characters...", ref voiceSearch, 128);
        ImGui.Checkbox("Show characters without an installed profile", ref showUnavailableVoices);
        var voices = snapshot.BaseVoices.Where(voice => (showUnavailableVoices || voice.Available)
                && voice.Label.Contains(voiceSearch.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(voice => voice.Label, StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = snapshot.BaseVoices.FirstOrDefault(voice => voice.Key == previewVoice);
        if (selected is null || !showUnavailableVoices && !selected.Available)
        {
            selected = voices.FirstOrDefault();
            if (selected is not null) previewVoice = selected.Key;
        }
        var width = ImGui.GetContentRegionAvail().X;
        if (ImGui.BeginChild("##VoiceList", new Vector2(width * .43f, Math.Max(220 * Scale,
                ImGui.GetContentRegionAvail().Y)), true))
        {
            foreach (var voice in voices)
            {
                ImGui.PushID(voice.Key);
                if (ImGui.Selectable(voice.Label, voice.Key == previewVoice))
                {
                    previewVoice = voice.Key;
                    selected = voice;
                    previewPlayed = false;
                }
                if (showUnavailableVoices && !voice.Available) ImGui.TextDisabled("No installed profile");
                ImGui.PopID();
            }
            if (voices.Length == 0)
                Wrapped(voiceSearch.Length > 0 ? "No matching characters." : "No installed voices in this language.", Muted);
        }
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##VoicePreview", Vector2.Zero, false))
        {
            if (selected is not null)
            {
                Wrapped(selected.Label, Accent);
                Wrapped(selected.Available ? "Installed" : "No installed profile", selected.Available ? Good : Muted);
                ImGui.Spacing();
                Wrapped("Sample text", Muted);
                ImGui.InputTextMultiline("##PreviewSentence", ref previewSentence, 2048, new Vector2(-1, 110 * Scale));
                var output = audioBackendStatus();
                ImGui.BeginDisabled(!selected.Available || !snapshot.Ready || snapshot.Running
                    || !output.Available || !output.Healthy || string.IsNullOrWhiteSpace(previewSentence));
                if (ImGui.Button("Play sample", new Vector2(-1, 0)))
                {
                    previewPlayed = true;
                    Run(() => runBaseDebug(previewVoice, previewSentence, voiceLanguage, shutdown.Token));
                }
                ImGui.EndDisabled();
                if (snapshot.Running)
                {
                    Wrapped("Preparing or playing a voice sample...", Accent);
                    if (ImGui.Button("Stop sample")) cancelDebug();
                }
                else if (previewPlayed) Wrapped(snapshot.Status);
                if (!snapshot.Ready) Wrapped(snapshot.Readiness, Muted);
                if (!output.Available || !output.Healthy) Wrapped(output.Diagnostic, Warning);
            }
            Wrapped("Profiles are supplied for characters with unvoiced dialogue. A missing profile does not mean their original game recordings are missing.", Muted);
            if (ImGui.Button("Refresh voices"))
            {
                refreshedVoiceLanguage = null;
                Volatile.Write(ref voiceRefreshFailed, false);
                EnsureVoiceList(voiceLanguage);
            }
            if (ImGui.CollapsingHeader("Audio capture"))
            {
                var export = configuration.ExportDebugBaseWav;
                if (ImGui.Checkbox("Save sample WAV files", ref export))
                {
                    configuration.ExportDebugBaseWav = export;
                    save();
                }
                Wrapped("Writes raw and corrected audio to the plugin's debug-audio folder.", Muted);
            }
        }
        ImGui.EndChild();
    }

    private void DrawModelPage()
    {
        if (BeginPanel("##Model", Vector2.Zero, true))
        {
            DrawQualityCards();
            ImGui.Dummy(new Vector2(0, 8 * Scale));
            ImGui.BeginDisabled(bootstrap().IsChangingQuality);
            DrawDeviceSelection();
            Divider();
            var keepLoaded = configuration.KeepBaseModelLoaded;
            if (ToggleRow("Keep model loaded", "Faster starts; reserves model memory while idle.", ref keepLoaded))
            {
                configuration.KeepBaseModelLoaded = keepLoaded;
                save();
                if (runtime() is { } manager) Run(() => manager.SetBaseHotLoadEnabledAsync(keepLoaded, shutdown.Token));
            }
            ImGui.EndDisabled();
        }
        EndPanel("##Model");
    }

    private void DrawPlaybackPage()
    {
        if (BeginPanel("##Playback", Vector2.Zero, true))
        {
            Heading("Playback");
            var enabled = configuration.Enabled;
            if (ToggleRow("Automatic dubbing", "Voice unvoiced NPC dialogue as you play.", ref enabled))
            {
                configuration.Enabled = enabled;
                save();
                preDubConfigurationChanged();
            }
            Divider();
            var autoAdvance = configuration.AutoAdvanceDubbedCutsceneDialogue;
            if (ToggleRow("Auto-advance", "Continue dubbed cutscene dialogue after speech ends.", ref autoAdvance))
            {
                configuration.AutoAdvanceDubbedCutsceneDialogue = autoAdvance;
                save();
            }
            Divider();
            var effects = configuration.EnableDialogueSoundEffects;
            if (ToggleRow("Dialogue sound effects", "Replace cues such as <sigh> with sounds. Other cues stay silent.", ref effects))
            {
                configuration.EnableDialogueSoundEffects = effects;
                save();
                dialogueSoundEffectsChanged();
            }
            ImGui.Dummy(new Vector2(0, 8 * Scale));
            Caption("Preparation");
            var preDub = configuration.PreDubUpcomingCutscenes;
            if (ToggleRow("Prepare upcoming story dialogue", "Cache main-story speech during safe downtime.", ref preDub))
            {
                configuration.PreDubUpcomingCutscenes = preDub;
                save();
                preDubConfigurationChanged();
            }
            Divider();
            var casting = configuration.BackgroundCasting;
            if (ToggleRow("Prepare background voices", "Prepare reusable voices for other NPCs during downtime.", ref casting))
            {
                configuration.BackgroundCasting = casting;
                save();
            }
            ImGui.Dummy(new Vector2(0, 8 * Scale));
            Caption("Audio processing");
            var correction = configuration.EnableVoiceCorrection;
            if (ToggleRow("Correction filter", "Adjust the tone of cloned voices.", ref correction))
            {
                configuration.EnableVoiceCorrection = correction;
                save();
            }
            Divider();
            var normalization = configuration.EnableLineNormalization;
            if (ToggleRow("Line normalization", "Even out cloned voice loudness and control peaks.", ref normalization))
            {
                configuration.EnableLineNormalization = normalization;
                save();
            }
            Divider();
            DrawVolume();
            DrawPreviewControl();
        }
        EndPanel("##Playback");
    }

    private void DrawAccessibilityPage()
    {
        if (BeginPanel("##Accessibility", Vector2.Zero, true))
        {
            Heading("Accessibility");
            var reduceMotion = configuration.ReduceMotion;
            if (ToggleRow("Reduce motion", "Freeze the visualizer and skip transitions.", ref reduceMotion))
            {
                configuration.ReduceMotion = reduceMotion;
                save();
            }
        }
        EndPanel("##Accessibility");
    }

    private void DrawSetupFeedback()
    {
        var boot = bootstrap();
        var output = audioBackendStatus();
        var failed = boot.Failure is not null || boot.State == BootstrapState.Failed;
        var downloading = boot.State is BootstrapState.DownloadingRuntime or BootstrapState.DownloadingBase;
        var outputFailure = boot.IsReady && (!output.Available || !output.Healthy);
        var speechSetup = !boot.IsReady || failed || outputFailure;
        var cuda = boot.CudaSetup;
        if (speechSetup || cuda is not null)
        {
            if (BeginPanel("##Setup", Vector2.Zero, true))
            {
                if (speechSetup)
                {
                    Heading(failed ? "Setup needs attention" : outputFailure ? "Voice output unavailable"
                        : downloading ? "Download in progress" : "Getting ready");
                    Wrapped(boot.Failure?.Message ?? (outputFailure ? output.Diagnostic : SetupDescription(boot.State)),
                        failed || outputFailure ? Warning : Muted);
                    if (boot.CurrentProgress is { } progress && downloading) DrawDownloadProgress(progress);
                }
                if (cuda is not null)
                {
                    if (speechSetup) ImGui.Spacing();
                    if (cuda.Error is { } error)
                    {
                        Heading("CUDA setup failed");
                        Wrapped(error, Warning);
                    }
                    else if (cuda.Progress is { } progress && progress.BytesReceived < progress.TotalBytes)
                    {
                        Heading("Downloading CUDA support");
                        DrawDownloadProgress(progress);
                    }
                    else
                    {
                        Heading("Preparing CUDA support");
                        Wrapped("A CUDA device was detected. Resonance checks the runtime pack before loading the backend.", Muted);
                    }
                }
            }
            EndPanel("##Setup");
        }
    }

    private void DrawDownloadProgress(AssetProgress progress)
    {
        var fraction = progress.TotalBytes <= 0 ? 0f
            : Math.Clamp((float)progress.BytesReceived / progress.TotalBytes, 0f, 1f);
        var total = progress.TotalBytes <= 0 ? $"{progress.BytesReceived / 1_000_000d:F0} MB downloaded"
            : $"{progress.BytesReceived / 1_000_000d:F0} / {progress.TotalBytes / 1_000_000d:F0} MB";
        ImGui.ProgressBar(fraction, new Vector2(-1, 20 * Scale), total);
    }

    private void DrawQualityCards()
    {
        ImGui.TextUnformatted("Quality");
        var available = ImGui.GetContentRegionAvail().X;
        var columns = available >= 4 * 160 * Scale + 24 * Scale ? 4 : 2;
        var width = (available - (columns - 1) * 8 * Scale) / columns;
        ImGui.BeginDisabled(bootstrap().IsChangingQuality || Volatile.Read(ref pendingQuality) >= 0);
        for (var index = 0; index < QualityLabels.Length; index++)
        {
            if (index % columns > 0) ImGui.SameLine();
            ImGui.PushID(index);
            var p = ImGui.GetCursorScreenPos();
            var size = new Vector2(width, 76 * Scale);
            if (ClickArea("##Quality", size) && configuration.Quality != (QualityPreset)index)
            {
                var target = (QualityPreset)index;
                Volatile.Write(ref pendingQuality, index);
                Run(async () =>
                {
                    try { await setQuality(target, shutdown.Token).ConfigureAwait(false); }
                    finally
                    {
                        Volatile.Write(ref pendingQuality, -1);
                        refreshedVoiceLanguage = null;
                        refreshedVoiceModel = null;
                    }
                });
            }
            if (ImGui.IsItemHovered()) hoveredQuality = (QualityPreset)index;
            var selected = SelectedQuality == (QualityPreset)index;
            var hover = Animate("hover", ImGui.IsItemHovered() || ImGui.IsItemFocused() ? 1 : 0);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(p, p + size, Color(new Vector4(.18f + hover * .025f, .20f + hover * .025f, .25f + hover * .025f, 1)), 8 * Scale);
            draw.AddRect(p, p + size, Color(selected ? Accent : new Vector4(.40f, .45f, .53f, .8f)), 8 * Scale, ImDrawFlags.None, selected ? 2 * Scale : Scale);
            using (headingFont.Push()) TextAt(p + new Vector2(12, 9) * Scale, QualityLabels[index], Text, fontScale: 1.1f);
            if (selected) IconAt(p + new Vector2(width - 26 * Scale, 13 * Scale), FontAwesomeIcon.Check, Accent);
            if (index == 0)
            {
                var badge = p + new Vector2(65, 12) * Scale;
                draw.AddRectFilled(badge, badge + new Vector2(48, 18) * Scale, Color(Accent with { W = .12f }), 9 * Scale);
                TextAt(badge + new Vector2(4, 1) * Scale, "Default", Accent, fontScale: .75f);
            }
            var asset = bootstrap().ModelAssets.FirstOrDefault(value => value.Id == ModelQualitySelection.Resolve((QualityPreset)index).Base);
            var spec = $"{(index < 2 ? "0.6B" : "1.7B")} / {(index % 2 == 0 ? "Q4" : "Q8")}";
            TextAt(p + new Vector2(12, 45) * Scale, spec, Muted, fontScale: CaptionScale);
            using (headingFont.Push())
            {
                var weight = asset is null ? "..." : $"{asset.Length / 1073741824d:F2} GiB";
                TextAt(p + new Vector2(width - 12 * Scale - ImGui.CalcTextSize(weight).X * 1.1f, 41 * Scale),
                    weight, selected ? Accent : Text, fontScale: 1.1f);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("0.6B and 1.7B are model parameter counts. Q4 and Q8 mean 4-bit and 8-bit weights; Q8 uses more memory. Sizes exclude runtime buffers and the decoder. Hover previews memory; click selects quality.");
            ImGui.PopID();
        }
        ImGui.EndDisabled();
    }

    private void DrawDeviceSelection()
    {
        ImGui.TextUnformatted("Device");
        var manager = runtime();
        var device = manager?.Selection?.Effective;
        var backendLabel = device?.Type.ToString().ToUpperInvariant() ?? "...";
        var chipWidth = ImGui.CalcTextSize(backendLabel).X + 20 * Scale;
        var comboWidth = ImGui.GetContentRegionAvail().X - chipWidth - 8 * Scale;
        var p = ImGui.GetCursorScreenPos();
        var automatic = configuration.Compute != ComputePreference.Manual;
        var label = device?.Description ?? "Detecting devices...";
        if (automatic) label = $"Auto ({label})";
        var icon = device?.Type == BackendType.Cpu ? FontAwesomeIcon.Desktop : FontAwesomeIcon.Microchip;
        ImGui.SetNextItemWidth(comboWidth);
        if (ImGui.BeginCombo("##ProcessingDevice", "", ImGuiComboFlags.NoArrowButton))
        {
            if (ImGui.Selectable("Auto (recommended)", automatic))
            {
                configuration.Compute = ComputePreference.AutoPerformance;
                save();
                if (manager is not null) Run(() => applyAutomaticBackend is null
                    ? manager.RefreshBackendsAsync(shutdown.Token) : applyAutomaticBackend(shutdown.Token));
            }
            foreach (var option in manager?.DetectedBackends ?? [])
                if (ImGui.Selectable($"{option.Description} {option.Type.ToString().ToUpperInvariant()}##{option.Name}", !automatic && device?.Name == option.Name))
                    Run(() => setBackend is null ? manager!.SetDesiredAsync(option, shutdown.Token) : setBackend(option, shutdown.Token));
            ImGui.EndCombo();
        }
        var centerY = ImGui.GetFrameHeight() / 2;
        CenteredIconAt(p + new Vector2(20 * Scale, centerY), icon, Accent);
        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(p + new Vector2(38 * Scale, 0), p + new Vector2(comboWidth - 28 * Scale, ImGui.GetFrameHeight()), true);
        TextAt(p + new Vector2(38, 8) * Scale, label, Text);
        draw.PopClipRect();
        CenteredIconAt(p + new Vector2(comboWidth - 16 * Scale, centerY), FontAwesomeIcon.ChevronDown, Muted);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
        ImGui.SameLine();
        var chip = ImGui.GetCursorScreenPos();
        draw.AddRectFilled(chip, chip + new Vector2(chipWidth, ImGui.GetFrameHeight()), Color(Accent with { W = .12f }), 8 * Scale);
        TextAt(chip + new Vector2(10, 8) * Scale, backendLabel, Accent);
        ImGui.Dummy(new Vector2(chipWidth, ImGui.GetFrameHeight()));
        if (device is not null) DrawMemoryProjection(device);
        if (manager?.Selection is { IsTemporaryCpuFallback: true } selection)
        {
            var preparingCuda = selection.Desired.Type == BackendType.Cuda && bootstrap().CudaSetup is { Error: null };
            Caption(preparingCuda ? "Using CPU while CUDA setup finishes." : "Temporary CPU fallback. See Debug for details.",
                preparingCuda ? Muted : Warning);
        }
    }

    private void DrawMemoryProjection(Resonance.Tts.BackendInfo selected)
    {
        var snapshot = runtime()?.DeviceMemory;
        var device = snapshot?.Devices.FirstOrDefault(value => value.Name == selected.Name);
        if (snapshot is null || device is null || device.MemoryTotal == 0)
        {
            Caption("Device memory is unavailable.");
            return;
        }
        var quality = hoveredQuality ?? SelectedQuality;
        var weights = bootstrap().ModelAssets.FirstOrDefault(value => value.Id == ModelQualitySelection.Resolve(quality).Base)?.Length ?? 0;
        var known = snapshot.CanDetermineInclusion(device);
        var includes = snapshot.IncludesModel(device);
        var projection = ModelMemoryProjection.Create(device.MemoryTotal, device.MemoryFree,
            includes ? snapshot.ResidentWeightsBytes : 0, known ? weights : 0);
        var modelColor = projection.Overflow > 0 ? Error : projection.NearCapacity ? Warning : Accent;
        var ghost = hoveredQuality is not null && quality != SelectedQuality;
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 18 * Scale);
        var draw = ImGui.GetWindowDrawList();
        var otherEnd = p.X + size.X * (float)(projection.Other / projection.Total);
        var modelEnd = Math.Min(p.X + size.X, otherEnd + size.X * (float)(projection.Model / projection.Total));
        var otherColor = new Vector4(.37f, .43f, .52f, 1);
        draw.AddRectFilled(p, p + size, Color(new Vector4(.055f, .065f, .085f, 1)), 6 * Scale);
        Segment(p.X, otherEnd, otherColor);
        Segment(otherEnd, modelEnd, modelColor with { W = ghost ? .4f : 1 });
        if (!configuration.KeepBaseModelLoaded || ghost) Hatch(otherEnd, modelEnd, Ink with { W = .45f });
        Hatch(modelEnd, p.X + size.X, modelColor with { W = .18f });
        ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
        {
            var inclusion = !known ? "Model inclusion is unknown."
                : includes ? "Snapshot includes the loaded model; the model segment estimates its weights."
                : "Snapshot precedes this model load; weights are added to usage.";
            var capacity = projection.Overflow > 0 ? $"Weights exceed capacity by {projection.Overflow / 1073741824d:F1} GiB."
                : $"{projection.Free / 1073741824d:F1} GiB remains before runtime/decoder overhead.";
            ImGui.SetTooltip($"{inclusion}\n{capacity}\nHatched weights load on demand. Hatching after the model marks unmeasured runtime/decoder overhead."
                + (ghost ? $"\nPreviewing {QualityLabels[(int)quality]}." : string.Empty));
        }
        if (ImGui.BeginTable("##MemoryLegend", 3, ImGuiTableFlags.SizingStretchSame))
        {
            Legend(known ? "Other" : "Used", $"{projection.Other / 1073741824d:F1}", otherColor, false);
            Legend("Resonance", known ? $"{projection.Model / 1073741824d:F2}" : "?", modelColor, false);
            Legend(projection.Overflow > 0 ? "Short" : "Free",
                $"{(projection.Overflow > 0 ? projection.Overflow : projection.Free) / 1073741824d:F1}",
                projection.Overflow > 0 ? Error : Muted, true);
            ImGui.EndTable();
        }
        void Segment(float start, float end, Vector4 color)
        {
            if (end <= start) return;
            draw.PushClipRect(new Vector2(start, p.Y), new Vector2(end, p.Y + size.Y), true);
            draw.AddRectFilled(p, p + size, Color(color), 6 * Scale);
            draw.PopClipRect();
        }
        void Hatch(float start, float end, Vector4 color)
        {
            if (end <= start) return;
            draw.PushClipRect(new Vector2(start, p.Y), new Vector2(end, p.Y + size.Y), true);
            for (var x = start - size.Y; x < end; x += 7 * Scale)
                draw.AddLine(new Vector2(x, p.Y + size.Y), new Vector2(x + size.Y, p.Y), Color(color), Scale);
            draw.PopClipRect();
        }
        void Legend(string label, string value, Vector4 color, bool outline)
        {
            ImGui.TableNextColumn();
            var at = ImGui.GetCursorScreenPos();
            if (outline) draw.AddCircle(at + new Vector2(4, 8) * Scale, 3 * Scale, Color(color));
            else draw.AddCircleFilled(at + new Vector2(4, 8) * Scale, 3 * Scale, Color(color));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 14 * Scale);
            Caption($"{label} {value} GiB", color);
        }
    }

    private void DrawVolume()
    {
        ImGui.Dummy(new Vector2(0, 8 * Scale));
        ImGui.TextUnformatted("Voice volume");
        ImGui.SameLine();
        var valueText = $"{configuration.Volume * 100:F0}%";
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(valueText).X);
        ImGui.TextColored(configuration.Volume > 1 ? Warm : Accent, valueText);
        var volume = configuration.Volume * 100;
        ImGui.SetNextItemWidth(-1);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        var p = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = ImGui.GetFrameHeight();
        var draw = ImGui.GetWindowDrawList();
        var a = p + new Vector2(0, height / 2 - 3 * Scale);
        var b = p + new Vector2(width, height / 2 + 3 * Scale);
        var center = (a.X + b.X) / 2;
        draw.AddRectFilled(a, b, Color(new Vector4(.24f, .27f, .33f, 1)), 3 * Scale);
        draw.AddRectFilled(new Vector2(center, a.Y), b, Color(Warm with { W = .28f }), 3 * Scale);
        var end = a.X + (b.X - a.X) * Math.Clamp(configuration.Volume / 2, 0, 1);
        RoundedGradient(a, new Vector2(Math.Min(end, center) - a.X, b.Y - a.Y), Accent, Accent, 3 * Scale);
        if (end > center) RoundedGradient(new Vector2(center, a.Y), new Vector2(end - center, b.Y - a.Y), Accent, Warm, 3 * Scale);
        draw.AddLine(new Vector2(center, a.Y - 6 * Scale), new Vector2(center, b.Y + 6 * Scale), Color(Text), 2 * Scale);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, configuration.Volume > 1 ? Warm : Accent);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, configuration.Volume > 1 ? Warm : Accent);
        if (ImGui.SliderFloat("##VoiceVolume", ref volume, 0f, 200f, string.Empty))
        {
            configuration.Volume = volume / 100;
            save();
        }
        ImGui.PopStyleColor(5);
        var markerY = ImGui.GetCursorScreenPos().Y;
        TextAt(new Vector2(a.X, markerY), "0%", Muted, fontScale: CaptionScale);
        TextAt(new Vector2(center - ImGui.CalcTextSize("100% Original").X * CaptionScale / 2, markerY),
            "100% Original", Text, fontScale: CaptionScale);
        TextAt(new Vector2(b.X - ImGui.CalcTextSize("200% / may clip").X * CaptionScale, markerY),
            "200% / may clip", Warm, fontScale: CaptionScale);
        ImGui.Dummy(new Vector2(width, ImGui.GetTextLineHeight() * CaptionScale));
        Caption("Boost above 100% may clip; output follows the game's Voice volume.");
    }

    private void EnsureVoiceList(string language)
    {
        var snapshot = debugSnapshot(language);
        if (!bootstrap().IsReady || !snapshot.Ready || refreshedVoiceLanguage == language && refreshedVoiceModel == runtime()?.ModelHash && snapshot.VoicesCached
            || Volatile.Read(ref voiceRefreshFailed)
            || Interlocked.CompareExchange(ref voiceRefreshInFlight, 1, 0) != 0) return;
        var modelHash = runtime()?.ModelHash;
        Run(async () =>
        {
            try
            {
                await refreshDebugBaseVoices(language, shutdown.Token).ConfigureAwait(false);
                refreshedVoiceLanguage = language;
                refreshedVoiceModel = modelHash;
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Volatile.Write(ref voiceRefreshFailed, true);
                throw;
            }
            finally { Volatile.Write(ref voiceRefreshInFlight, 0); }
        });
    }

    private static void Section(string label)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Muted, label);
    }

    private static void Wrapped(string text, Vector4? color = null)
    {
        if (color.HasValue) ImGui.PushStyleColor(ImGuiCol.Text, color.Value);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        if (color.HasValue) ImGui.PopStyleColor();
    }

    private static string SetupDescription(BootstrapState state) => state switch
    {
        BootstrapState.DownloadingRuntime => "Downloading the speech engine. Setup continues in the background.",
        BootstrapState.DownloadingBase => "Downloading character voice models. Setup continues in the background.",
        BootstrapState.BenchmarkingBackends => "Measuring your devices to choose where speech runs best.",
        BootstrapState.Stopped => "The speech engine has stopped. Reload Resonance to start it again.",
        _ => "Loading the speech engine and voice models.",
    };
}
