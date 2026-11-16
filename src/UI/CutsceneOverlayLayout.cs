using System.Numerics;

namespace Resonance.UI;

public static class CutsceneOverlayLayout
{
    // ForceMainWindow adds the viewport origin when applying Window.Position.
    public static Vector2 AbovePanel(Vector2 viewportOrigin, Vector2 viewportSize,
        Vector2 panelTopRight, Vector2 buttonSize, float gap) =>
        Vector2.Clamp(panelTopRight - new Vector2(buttonSize.X, buttonSize.Y + gap),
            viewportOrigin, viewportOrigin + Vector2.Max(Vector2.Zero, viewportSize - buttonSize)) - viewportOrigin;
}
