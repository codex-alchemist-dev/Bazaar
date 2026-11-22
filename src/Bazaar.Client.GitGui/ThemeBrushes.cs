using Avalonia;
using Avalonia.Media;

namespace Bazaar.Client.GitGui;

/// <summary>
/// Reads brushes from the theme tokens (one theme file drives every head; doc 27, doc 41). The views never use a fixed
/// colour. A token the active theme does not define returns null, and the caller leaves the property to inherit.
/// </summary>
internal static class ThemeBrushes
{
    internal const string TextPrimary = "Bazaar.brush.color.text.primary";
    internal const string TextSecondary = "Bazaar.brush.color.text.secondary";
    internal const string TextAccent = "Bazaar.brush.color.text.accent";
    internal const string Border = "Bazaar.brush.color.border.default";

    // The theme has no diff tokens yet. Added and removed lines borrow the syntax string and keyword tokens; the +/- marker
    // in each row carries the meaning, so colour is never the only cue. Dedicated diff tokens need a theme-schema change.
    internal const string Added = "Bazaar.brush.color.syntax.string";
    internal const string Removed = "Bazaar.brush.color.syntax.keyword";

    internal static IBrush? Find(string token)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush brush ? brush : null;
    }
}
