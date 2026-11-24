using Avalonia.Controls;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Materials.Avalonia;

/// <summary>Maps a material decision onto Avalonia: the window's transparency hint and the surface brushes.</summary>
public static class MaterialApplier
{
    /// <summary>
    /// The TransparencyLevelHint for a material. Avalonia uses the first level the platform supports, so Mica falls back to
    /// acrylic and then to opaque, as the doc requires. A tinted surface needs only plain transparency.
    /// </summary>
    public static IReadOnlyList<WindowTransparencyLevel> HintFor(MaterialKind kind) => kind switch
    {
        MaterialKind.Mica => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
        MaterialKind.Acrylic => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None],
        MaterialKind.Tinted => [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None],
        _ => [WindowTransparencyLevel.None],
    };

    /// <summary>
    /// Applies the chrome decision to a window: its backdrop hint and its background. The background is the translucent tint
    /// for a translucent kind and the opaque fallback otherwise.
    /// </summary>
    public static void ApplyToWindow(Window window, IMaterialPolicy policy, ThemeDefinition theme)
    {
        MaterialDecision decision = policy.Resolve(MaterialSurface.Chrome);
        window.TransparencyLevelHint = HintFor(decision.Kind);
        window.Background = MaterialBrushes.For(theme, MaterialSurface.Chrome, decision);
    }

    /// <summary>Applies the decision for <paramref name="surface"/> to a panel's background.</summary>
    public static void ApplyToSurface(Panel panel, MaterialSurface surface, IMaterialPolicy policy, ThemeDefinition theme) =>
        panel.Background = MaterialBrushes.For(theme, surface, policy.Resolve(surface));
}
