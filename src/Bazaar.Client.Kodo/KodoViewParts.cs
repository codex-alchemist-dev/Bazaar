using Avalonia;
using Avalonia.Automation;
using Avalonia.Media;

namespace Bazaar.Client.Kodo;

/// <summary>Theme lookups shared by the Kodo views. Colours come from theme tokens only (one theme file drives every head).</summary>
internal static class KodoViewParts
{
    internal const string SecondaryTextToken = "Bazaar.brush.color.text.secondary";
    internal const string BorderToken = "Bazaar.brush.color.border.default";

    /// <summary>The brush for a token, or for the fallback token, or transparent when the theme has neither.</summary>
    internal static IBrush Brush(string token, string fallbackToken = KodoStateTokens.Fallback)
    {
        var app = Application.Current;
        if (app is null)
        {
            return Brushes.Transparent;
        }

        foreach (var key in new[] { token, fallbackToken })
        {
            if (app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush)
            {
                return brush;
            }
        }

        return Brushes.Transparent;
    }

    internal static void Name(StyledElement target, string name) => AutomationProperties.SetName(target, name);
}
