using Avalonia;
using Avalonia.Media;
using Bazaar.Client.Calcinato.Emulation;

namespace Bazaar.Client.Calcinato.Avalonia;

/// <summary>
/// The colours a terminal draws with (doc 55, section 5.4). Each one comes from a theme token (<c>Terminal.Foreground</c>,
/// <c>Terminal.Background</c>, <c>Terminal.Ansi0</c> to <c>Terminal.Ansi15</c>, <c>Terminal.Cursor</c>,
/// <c>Terminal.Selection</c>). A token the theme does not define falls back to a built-in value, so the terminal always draws.
/// </summary>
public sealed class TerminalPalette
{
    /// <summary>Theme token names, in the order the palette reads them.</summary>
    public const string ForegroundToken = "Terminal.Foreground";

    /// <summary>Theme token for the default background.</summary>
    public const string BackgroundToken = "Terminal.Background";

    /// <summary>Theme token for the cursor block or bar.</summary>
    public const string CursorToken = "Terminal.Cursor";

    /// <summary>Theme token for the Visual selection highlight.</summary>
    public const string SelectionToken = "Terminal.Selection";

    /// <summary>Theme token for the status line background.</summary>
    public const string StatusToken = "Terminal.StatusBackground";

    private static readonly Color[] _fallbackAnsi =
    [
        Color.FromRgb(0x00, 0x00, 0x00), Color.FromRgb(0xCD, 0x31, 0x31), Color.FromRgb(0x0D, 0xBC, 0x79), Color.FromRgb(0xE5, 0xE5, 0x10),
        Color.FromRgb(0x24, 0x72, 0xC8), Color.FromRgb(0xBC, 0x3F, 0xBC), Color.FromRgb(0x11, 0xA8, 0xCD), Color.FromRgb(0xE5, 0xE5, 0xE5),
        Color.FromRgb(0x66, 0x66, 0x66), Color.FromRgb(0xF1, 0x4C, 0x4C), Color.FromRgb(0x23, 0xD1, 0x8B), Color.FromRgb(0xF5, 0xF5, 0x43),
        Color.FromRgb(0x3B, 0x8E, 0xEA), Color.FromRgb(0xD6, 0x70, 0xD6), Color.FromRgb(0x29, 0xB8, 0xDB), Color.FromRgb(0xFF, 0xFF, 0xFF),
    ];

    // The xterm 6x6x6 cube levels for palette indices 16 to 231.
    private static readonly byte[] _cubeLevels = [0, 95, 135, 175, 215, 255];

    private readonly Color[] _ansi;

    private TerminalPalette(Color foreground, Color background, Color cursor, Color selection, Color status, Color[] ansi)
    {
        Foreground = foreground;
        Background = background;
        Cursor = cursor;
        Selection = selection;
        StatusBackground = status;
        _ansi = ansi;
    }

    /// <summary>The default foreground.</summary>
    public Color Foreground { get; }

    /// <summary>The default background.</summary>
    public Color Background { get; }

    /// <summary>Cursor colour.</summary>
    public Color Cursor { get; }

    /// <summary>Visual selection colour.</summary>
    public Color Selection { get; }

    /// <summary>Status line background.</summary>
    public Color StatusBackground { get; }

    /// <summary>The 16 base colours, in SGR order (black, red, ..., bright white).</summary>
    public IReadOnlyList<Color> Ansi => _ansi;

    /// <summary>The built-in palette, used when the theme defines none of the tokens.</summary>
    public static TerminalPalette Fallback { get; } = new(
        foreground: Color.FromRgb(0xD4, 0xD4, 0xD4),
        background: Color.FromRgb(0x1E, 0x1E, 0x1E),
        cursor: Color.FromRgb(0xAE, 0xAF, 0xAD),
        selection: Color.FromRgb(0x26, 0x4F, 0x78),
        status: Color.FromRgb(0x00, 0x7A, 0xCC),
        ansi: [.. _fallbackAnsi]);

    /// <summary>Reads the palette from the application's theme. Each missing token uses its fallback.</summary>
    public static TerminalPalette FromTheme()
    {
        Color[] ansi = new Color[16];
        for (int i = 0; i < ansi.Length; i++)
        {
            ansi[i] = ThemeColor($"Terminal.Ansi{i}") ?? _fallbackAnsi[i];
        }

        return new TerminalPalette(
            foreground: ThemeColor(ForegroundToken) ?? Fallback.Foreground,
            background: ThemeColor(BackgroundToken) ?? Fallback.Background,
            cursor: ThemeColor(CursorToken) ?? Fallback.Cursor,
            selection: ThemeColor(SelectionToken) ?? Fallback.Selection,
            status: ThemeColor(StatusToken) ?? Fallback.StatusBackground,
            ansi: ansi);
    }

    /// <summary>
    /// The colour of a cell colour. Default maps to the foreground or background, a palette index below 16 to the theme's
    /// base colours, the rest of the 256 palette is computed, and truecolour passes through.
    /// </summary>
    /// <param name="color">The cell colour.</param>
    /// <param name="foreground">True for a foreground role, which decides what Default means.</param>
    public Color Resolve(TermColor color, bool foreground)
    {
        switch (color.Kind)
        {
            case TermColorKind.Indexed:
                return color.Index < 16 ? _ansi[color.Index] : Indexed256(color.Index);
            case TermColorKind.Rgb:
                return Color.FromRgb(color.Red, color.Green, color.Blue);
            default:
                return foreground ? Foreground : Background;
        }
    }

    /// <summary>
    /// The colour of a 256-colour palette index from 16 up: the 6x6x6 colour cube (16 to 231) or the grey ramp (232 to 255).
    /// Indices below 16 are the theme's base colours and are not computed here.
    /// </summary>
    /// <param name="index">Palette index, 16 to 255.</param>
    public static Color Indexed256(byte index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, (byte)16);
        if (index >= 232)
        {
            byte grey = (byte)(8 + (10 * (index - 232)));
            return Color.FromRgb(grey, grey, grey);
        }

        int n = index - 16;
        return Color.FromRgb(_cubeLevels[n / 36], _cubeLevels[(n / 6) % 6], _cubeLevels[n % 6]);
    }

    private static Color? ThemeColor(string token)
    {
        object? value = ThemeResources.Find(token);
        return value switch
        {
            Color color => color,
            ISolidColorBrush brush => brush.Color,
            _ => null,
        };
    }
}

/// <summary>Reads theme resources from the application. Returns null when there is no application or no such token.</summary>
internal static class ThemeResources
{
    public static object? Find(string token)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return null;
        }

        return app.TryGetResource(token, app.ActualThemeVariant, out object? value) ? value : null;
    }
}
