using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Resonance.Scheduling;

namespace Resonance.UI;

public sealed class CutsceneControlOverlay : Window
{
    private static readonly Vector4 Gold = new(.85f, .73f, .47f, 1f);
    private readonly IGameGui gameGui;
    private readonly Func<CutsceneControlSnapshot> getSnapshot;
    private readonly Action<CutsceneControlSnapshot, bool> setPaused;
    private CutsceneControlSnapshot snapshot = CutsceneControlSnapshot.Hidden;
    private Vector2 buttonSize;

    public CutsceneControlOverlay(IGameGui gameGui, Func<CutsceneControlSnapshot> getSnapshot,
        Action<CutsceneControlSnapshot, bool> setPaused)
        : base("##ResonanceCutsceneControl", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNavFocus | ImGuiWindowFlags.NoBackground)
    {
        this.gameGui = gameGui;
        this.getSnapshot = getSnapshot;
        this.setPaused = setPaused;
        IsOpen = true;
        ShowCloseButton = false;
        RespectCloseHotkey = false;
        AllowPinning = false;
        AllowClickthrough = false;
        AllowBackgroundBlur = false;
        DisableWindowSounds = true;
        DisableFadeInFadeOut = true;
        ForceMainWindow = true;
        PositionCondition = ImGuiCond.Always;
    }

    public override unsafe bool DrawConditions()
    {
        snapshot = getSnapshot();
        if (!snapshot.Visible) return false;
        var addon = (AddonTalk*)gameGui.GetAddonByName("Talk").Address;
        if (addon == null || !addon->IsVisible || addon->RootNode == null) return false;
        var scale = ImGuiHelpers.GlobalScale;
        buttonSize = new Vector2(ImGui.CalcTextSize("Resonance").X + 44 * scale, 29 * scale);
        var viewport = ImGui.GetMainViewport();
        var topRight = viewport.Pos + new Vector2(
            addon->RootNode->ScreenX + addon->GetScaledWidth(true), addon->RootNode->ScreenY);
        Position = CutsceneOverlayLayout.AbovePanel(viewport.Pos, viewport.Size, topRight, buttonSize, 7 * scale);
        return true;
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 15 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(12, 5) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0, .5f));
        ImGui.PushStyleColor(ImGuiCol.Text, snapshot.Paused ? new Vector4(.69f, .72f, .77f, 1) : Gold);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(.10f, .115f, .14f, .94f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(.22f, .22f, .20f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(.30f, .27f, .20f, 1));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(4);
        ImGui.PopStyleVar(5);
    }

    public override void Draw()
    {
        var origin = ImGui.GetCursorScreenPos();
        if (ImGui.Button("Resonance###ScenePlayback", buttonSize)) setPaused(snapshot, !snapshot.Paused);
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var color = ImGui.GetColorU32(snapshot.Paused ? new Vector4(.69f, .72f, .77f, 1) : Gold);
        draw.AddRect(origin, origin + buttonSize, color, 15 * scale);
        var center = origin + new Vector2(buttonSize.X - 16 * scale, buttonSize.Y / 2);
        if (snapshot.Paused)
            draw.AddTriangleFilled(center + new Vector2(-3, -5) * scale,
                center + new Vector2(-3, 5) * scale, center + new Vector2(5, 0) * scale, color);
        else
        {
            draw.AddRectFilled(center + new Vector2(-4, -5) * scale, center + new Vector2(-1, 5) * scale, color);
            draw.AddRectFilled(center + new Vector2(2, -5) * scale, center + new Vector2(5, 5) * scale, color);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(snapshot.Paused
            ? "Resume Resonance for this cutscene. The current line starts again."
            : "Pause Resonance for this cutscene. Stop speech and generation; use the game's normal pacing.");
    }
}
