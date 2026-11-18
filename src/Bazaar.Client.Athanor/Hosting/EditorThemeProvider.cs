using Bazaar.Client.Core.Theme;

namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// Turns the client's current theme into a Monaco defineTheme payload and pushes it to an editor host (doc 52 §3).
/// The payload comes from <see cref="MonacoThemeGenerator"/>, so Athanor has no second palette (doc 41 §4.15).
/// The last payload is cached by theme identity: the same <see cref="ThemeDefinition"/> instance gives the same JSON
/// without regenerating it. Single-threaded, like the UI thread that calls it.
/// </summary>
public sealed class EditorThemeProvider
{
    private readonly Func<ThemeDefinition> _current;
    private ThemeDefinition? _cachedTheme;
    private string? _cachedJson;

    public EditorThemeProvider(Func<ThemeDefinition> current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _current = current;
    }

    /// <summary>The Monaco defineTheme JSON for the current theme. Regenerated only when the theme instance changes.</summary>
    public string GetMonacoThemeJson()
    {
        ThemeDefinition theme = _current();
        if (_cachedJson is null || !ReferenceEquals(theme, _cachedTheme))
        {
            _cachedJson = MonacoThemeGenerator.Generate(theme);
            _cachedTheme = theme;
        }

        return _cachedJson;
    }

    /// <summary>Pushes the current theme into <paramref name="host"/> without reloading the editor.</summary>
    public Task PushAsync(IEditorHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        return host.SetThemeAsync(GetMonacoThemeJson(), cancellationToken);
    }
}
