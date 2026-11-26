namespace Bazaar.Client.Tui;

/// <summary>
/// The one built-in look (doc 58 section 4, owner card 2026-11-22: fixed, not themeable). Colour is only ever an addition: every
/// state word is also written as text. With colour off (<c>NO_COLOR</c>, a pipe) every method returns the text unchanged.
/// </summary>
public sealed class TuiStyle(bool useColor)
{
    private const string _reset = "\u001b[0m";

    public static TuiStyle Plain { get; } = new(false);

    public bool UseColor => useColor;

    public string Error(string text) => Wrap("\u001b[31m", text);

    public string Warning(string text) => Wrap("\u001b[33m", text);

    public string Dim(string text) => Wrap("\u001b[2m", text);

    public string Bold(string text) => Wrap("\u001b[1m", text);

    private string Wrap(string code, string text) => useColor ? code + text + _reset : text;
}
