using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Bazaar.Client.PhoneCoach.Avalonia;

/// <summary>Looks a brush up by theme resource key. A running theme that lacks the key gets a named system brush, never a literal colour.</summary>
internal static class ThemeTokens
{
    public static IBrush Brush(string key, IBrush fallback) =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var found) && found is IBrush brush
            ? brush
            : fallback;
}
