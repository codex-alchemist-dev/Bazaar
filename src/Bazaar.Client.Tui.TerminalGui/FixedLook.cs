using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>
/// The one built-in dark look (doc 58 section 4, owner card 2026-11-22: fixed, not themeable, no Bazaar theme file). Every view of this
/// module gets one of these schemes set explicitly, so a Terminal.Gui configuration file on the box cannot change the look. Colour
/// only ever adds to text: every state word (the alert kind, the selected menu) is also written out.
/// </summary>
internal static class FixedLook
{
    private static readonly Color _black = new(ColorName16.Black);
    private static readonly Color _white = new(ColorName16.White);
    private static readonly Color _gray = new(ColorName16.Gray);
    private static readonly Color _darkGray = new(ColorName16.DarkGray);
    private static readonly Color _red = new(ColorName16.Red);
    private static readonly Color _green = new(ColorName16.Green);
    private static readonly Color _cyan = new(ColorName16.Cyan);

    /// <summary>Body text and dialogs: light grey on black.</summary>
    public static Scheme Body { get; } = Build(new Attribute(_gray, _black), new Attribute(_black, _cyan), new Attribute(_white, _black, TextStyle.Bold));

    /// <summary>The menu bar, the status line and the dropdown: black on light grey.</summary>
    public static Scheme Bar { get; } = Build(new Attribute(_black, _gray), new Attribute(_white, _black), new Attribute(_black, _gray, TextStyle.Bold));

    /// <summary>A serious alert banner: white on red, bold.</summary>
    public static Scheme Serious { get; } = Build(new Attribute(_white, _red, TextStyle.Bold), new Attribute(_red, _white), new Attribute(_white, _red, TextStyle.Bold));

    /// <summary>A health-win banner: black on green.</summary>
    public static Scheme Win { get; } = Build(new Attribute(_black, _green), new Attribute(_green, _black), new Attribute(_black, _green, TextStyle.Bold));

    private static Scheme Build(Attribute normal, Attribute focus, Attribute hot) => new()
    {
        Normal = normal,
        HotNormal = hot,
        Focus = focus,
        HotFocus = focus,
        Active = focus,
        HotActive = focus,
        Highlight = focus,
        Editable = normal,
        ReadOnly = new Attribute(_darkGray, normal.Background),
        Disabled = new Attribute(_darkGray, normal.Background),
    };
}
