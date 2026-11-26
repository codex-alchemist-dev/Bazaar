namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>Which banner the shell shows (doc 58 section 3a).</summary>
public enum ShellAlertKind
{
    /// <summary>A serious failure (Kodo's top grade): a red banner, the terminal bell once, and it stays until acknowledged.</summary>
    Serious,

    /// <summary>A bigger health win, such as a survived fatal crash: a green banner until dismissed.</summary>
    Win,
}

/// <summary>
/// One banner for the top of the screen. The kind is always written as a word (<c>SERIOUS</c> or <c>OK</c>) as well as coloured, since
/// colour is never the only signal. Minor failures and small wins are not banners: they are lines on the overview, which is content.
/// </summary>
public sealed record ShellAlert(ShellAlertKind Kind, string Text);
