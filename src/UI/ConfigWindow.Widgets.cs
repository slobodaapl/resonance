using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace Resonance.UI;

public sealed partial class ConfigWindow
{
    private readonly Dictionary<uint, float> transitions = [];
    private readonly Dictionary<string, float> panelHeights = [];
    private readonly HashSet<string> measuredPanels = [];
    private readonly float[] signalHistory = new float[256];
    private double nextSignalSample;
    private const float CaptionScale = .92f;

    private float Animate(string id, float target)
    {
        var key = ImGui.GetID(id);
        var value = transitions.GetValueOrDefault(key, target);
        value = configuration.ReduceMotion ? target
            : target + (value - target) * MathF.Exp(-ImGui.GetIO().DeltaTime / .055f);
        transitions[key] = value;
        return value;
    }

    private bool BeginPanel(string id, Vector2 size, bool autoHeight = false, bool inset = false, bool border = true)
    {
        // This Dalamud build predates auto-sizing child windows; measure content for the next frame.
        if (autoHeight) size.Y = panelHeights[id] = panelHeights.GetValueOrDefault(id, 240 * Scale);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, inset ? new Vector4(.16f, .18f, .22f, 1) : new Vector4(.105f, .120f, .148f, 1));
        var visible = ImGui.BeginChild(id, size, border && !inset, ImGuiWindowFlags.AlwaysUseWindowPadding
            | (autoHeight ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None));
        ImGui.PopStyleColor();
        if (visible)
        {
            if (autoHeight) measuredPanels.Add(id);
            var p = ImGui.GetWindowPos();
            var width = ImGui.GetWindowSize().X;
            if (border && !inset) ImGui.GetWindowDrawList().AddLine(p + new Vector2(12, 1) * Scale,
                p + new Vector2(width - 12 * Scale, Scale), Color(new Vector4(.31f, .33f, .38f, .45f)));
        }
        return visible;
    }

    private void EndPanel(string id)
    {
        if (measuredPanels.Remove(id))
            panelHeights[id] = ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y + ImGui.GetStyle().WindowPadding.Y;
        ImGui.EndChild();
    }

    private void Heading(string text)
    {
        using (headingFont.Push()) ImGui.TextColored(Accent, text);
        ImGui.Dummy(new Vector2(0, 4 * Scale));
    }

    private static uint Color(Vector4 color) => ImGui.GetColorU32(color);

    private static void RoundedGradient(Vector2 p, Vector2 size, Vector4 left, Vector4 right, float radius)
    {
        if (size.X <= 0 || size.Y <= 0) return;
        radius = Math.Min(radius, Math.Min(size.X, size.Y) / 2);
        var draw = ImGui.GetWindowDrawList();
        var firstVertex = draw.VtxBuffer.Size;
        draw.AddRectFilled(p, p + size, uint.MaxValue, radius);
        foreach (ref var vertex in draw.VtxBuffer.AsSpan()[firstVertex..])
        {
            var fraction = Math.Clamp((vertex.Pos.X - p.X) / size.X, 0, 1);
            var color = Vector4.Lerp(left, right, fraction);
            // Keep the mesh's anti-alias coverage instead of painting overlapping translucent shapes.
            color.W *= (vertex.Col >> 24) / 255f;
            vertex.Col = Color(color);
        }
    }

    private static void Caption(string text, Vector4? color = null)
    {
        var width = ImGui.GetContentRegionAvail().X;
        TextAt(ImGui.GetCursorScreenPos(), text, color ?? Muted, width, CaptionScale);
        ImGui.Dummy(new Vector2(width, ImGui.CalcTextSize(text, false, width / CaptionScale).Y * CaptionScale));
    }

    private static void Divider()
    {
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p, p + new Vector2(ImGui.GetContentRegionAvail().X, 0),
            Color(new Vector4(.27f, .30f, .36f, .55f)), Scale);
        ImGui.Dummy(new Vector2(0, Scale));
    }

    private static bool ClickArea(string id, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Zero);
        var clicked = ImGui.Button(id, size);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    private static void TextAt(Vector2 position, string text, Vector4 color, float wrap = 0, float fontScale = 1) =>
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * fontScale, position,
            Color(color), text, wrap);

    private static void IconAt(Vector2 position, FontAwesomeIcon icon, Vector4 color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        TextAt(position, icon.ToIconString(), color);
        ImGui.PopFont();
    }

    private static void CenteredIconAt(Vector2 center, FontAwesomeIcon icon, Vector4 color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var text = icon.ToIconString();
        TextAt(center - ImGui.CalcTextSize(text) / 2, text, color);
        ImGui.PopFont();
    }

    private bool NavigationRow(string label, FontAwesomeIcon icon, int index)
    {
        ImGui.PushID(label);
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 42 * Scale);
        var clicked = ClickArea("##Navigation", size);
        var amount = Animate("selected", page == index ? 1 : 0);
        var hover = Animate("hover", ImGui.IsItemHovered() || ImGui.IsItemFocused() ? 1 : 0);
        var draw = ImGui.GetWindowDrawList();
        RoundedGradient(p, size, Accent with { W = amount * .07f + hover * .06f },
            Warm with { W = amount * .10f + hover * .04f }, 0);
        draw.AddLine(p + new Vector2(0, size.Y), p + size,
            Color(new Vector4(.27f, .30f, .36f, .45f)), Scale);
        if (amount > .01f)
            draw.AddLine(p + new Vector2(1.5f, 10) * Scale,
                p + new Vector2(1.5f * Scale, size.Y - 10 * Scale), Color(Accent with { W = amount }), 3 * Scale);
        IconAt(p + new Vector2(16, 12) * Scale, icon, page == index ? Accent : Muted);
        TextAt(p + new Vector2(43, 11) * Scale, label, page == index ? Text : Muted);
        ImGui.PopID();
        return clicked;
    }

    private bool ToggleRow(string title, string description, ref bool value)
    {
        ImGui.PushID(title);
        var p = ImGui.GetCursorScreenPos();
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 0);
        var titleWidth = Math.Max(80 * Scale, size.X - 64 * Scale);
        var descriptionWidth = size.X;
        var titleSize = ImGui.CalcTextSize(title, false, titleWidth);
        var descriptionY = 4 * Scale + titleSize.Y;
        var descriptionSize = ImGui.CalcTextSize(description, false, descriptionWidth / CaptionScale) * CaptionScale;
        var textHeight = descriptionY + descriptionSize.Y;
        size.Y = Math.Max(40 * Scale, textHeight);
        var textTop = (size.Y - textHeight) / 2;
        var changed = ClickArea("##SwitchRow", size);
        if (changed) value = !value;
        var draw = ImGui.GetWindowDrawList();
        var hover = Animate("hover", ImGui.IsItemHovered() || ImGui.IsItemFocused() ? 1 : 0);
        draw.AddRectFilled(p - new Vector2(4, 0) * Scale, p + size + new Vector2(4, 0) * Scale,
            Color(new Vector4(1, 1, 1, hover * .075f)), 8 * Scale);
        TextAt(p + new Vector2(0, textTop), title, Text, titleWidth);
        TextAt(p + new Vector2(0, textTop + descriptionY), description, Muted, descriptionWidth, CaptionScale);
        var knob = Animate("knob", value ? 1 : 0);
        var track = p + new Vector2(size.X - 44 * Scale, (size.Y - 24 * Scale) / 2);
        if (value) draw.AddRectFilled(track - new Vector2(3) * Scale,
            track + new Vector2(47, 27) * Scale, Color(Accent with { W = .08f }), 16 * Scale);
        draw.AddRectFilled(track, track + new Vector2(44, 24) * Scale,
            Color(Vector4.Lerp(new Vector4(.23f, .26f, .31f, 1), Accent, knob)), 12 * Scale);
        draw.AddCircleFilled(track + new Vector2(12 + 20 * knob, 12) * Scale, 8 * Scale,
            Color(Text));
        ImGui.PopID();
        return changed;
    }

    private static float SignalLevel(float peak) => peak <= 0 ? 0
        : Math.Clamp((20 * MathF.Log10(peak) + 60) / 60, 0, 1);

    private void DrawSignal(Vector2 p, Vector2 size, float? peak)
    {
        var draw = ImGui.GetWindowDrawList();
        if (frameError is not null)
        {
            draw.AddLine(p + new Vector2(0, size.Y / 2), p + new Vector2(size.X, size.Y / 2), Color(Error), 2 * Scale);
            return;
        }
        var time = ImGui.GetTime();
        if (!configuration.ReduceMotion && time >= nextSignalSample)
        {
            Array.Copy(signalHistory, 1, signalHistory, 0, signalHistory.Length - 1);
            signalHistory[^1] = Math.Max(peak ?? 0, 0);
            nextSignalSample = time + .04;
        }
        var count = Math.Min(signalHistory.Length, Math.Max(0, (int)(size.X / (7 * Scale))));
        for (var index = 0; index < count; index++)
        {
            var phase = configuration.ReduceMotion ? 0 : (float)time * 1.5f;
            var idle = .12f + .32f * MathF.Abs(MathF.Sin(phase + index * .15f));
            var sample = signalHistory[signalHistory.Length - count + index];
            var height = size.Y * Math.Max(idle, SignalLevel(sample));
            var x = p.X + index * 7 * Scale;
            var color = sample > 1 && configuration.Volume > 1 ? Error
                : Vector4.Lerp(Accent, Warm, count <= 1 ? 0 : (float)index / (count - 1));
            draw.AddRectFilled(new Vector2(x, p.Y + (size.Y - height) / 2),
                new Vector2(x + 4 * Scale, p.Y + (size.Y + height) / 2), Color(color), 2 * Scale);
        }
    }
}
