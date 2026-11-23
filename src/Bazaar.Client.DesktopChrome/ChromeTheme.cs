using System.Globalization;
using Avalonia.Media;
using Bazaar.Client.Theme;

namespace Bazaar.Client.DesktopChrome;

/// <summary>
/// Resolves theme tokens into Avalonia values for the chrome controls. The controls name tokens only and never carry a colour,
/// duration or size of their own (doc 93, Everything is a Rubedo-overridable token); this is the token-resolution point, so the
/// literal scan allows this file. A token the theme does not hold throws, so a broken theme fails closed.
/// </summary>
public sealed class ChromeTheme
{
    public ChromeTheme(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Definition = theme;
    }

    public ThemeDefinition Definition { get; private set; }

    /// <summary>Raised after <see cref="Use"/>; the controls re-read their colours.</summary>
    public event EventHandler? Changed;

    /// <summary>Switches to another theme (a person changed theme, or a Rubedo override arrived).</summary>
    public void Use(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        Definition = theme;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IBrush Brush(string token) => new SolidColorBrush(Colour(token));

    /// <summary>The theme colour drawn at <paramref name="opacity"/> (used for the scrim).</summary>
    public IBrush Brush(string token, double opacity) => new SolidColorBrush(Colour(token), Math.Clamp(opacity, 0.0, 1.0));

    public double Length(string token) => Number(token);

    public double Number(string token) => double.Parse(Raw(token), CultureInfo.InvariantCulture);

    public TimeSpan Duration(string token) => TimeSpan.FromMilliseconds(Number(token));

    public FontFamily Family(string token) => new(Raw(token));

    // The theme file writes #RRGGBBAA, but Avalonia reads eight hex digits as #AARRGGBB, so the alpha pair moves to the front.
    private Color Colour(string token)
    {
        string value = Raw(token);
        return value.Length == 9 ? Color.Parse("#" + value[7..9] + value[1..7]) : Color.Parse(value);
    }

    private string Raw(string token) =>
        Definition.Tokens.TryGetValue(token, out string? value)
            ? value
            : throw new InvalidOperationException($"The theme does not set token '{token}'.");
}
