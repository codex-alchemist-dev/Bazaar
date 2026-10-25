using Bazaar.Client.Core.Input;

namespace Bazaar.Client.Core.Tests.Input;

internal static class InputTestData
{
    public static readonly KeyStroke Enter = KeyStroke.OfKey(NamedKey.Enter);
    public static readonly KeyStroke Escape = KeyStroke.OfKey(NamedKey.Escape);
    public static readonly KeyStroke Backspace = KeyStroke.OfKey(NamedKey.Backspace);
    public static readonly KeyStroke Down = KeyStroke.OfKey(NamedKey.Down);
    public static readonly KeyStroke Up = KeyStroke.OfKey(NamedKey.Up);
    public static readonly KeyStroke Space = KeyStroke.OfChar(' ');

    public static KeyStroke C(char c) => KeyStroke.OfChar(c);

    public static IEnumerable<KeyStroke> Keys(string text) => text.Select(c => KeyStroke.OfChar(c));

    /// <summary>Palette is Space Space. "w" is a prefix of "wc" and "wi"; "gc" is unrelated.</summary>
    public static MnemonicTable Table()
    {
        var result = MnemonicTable.Create(
            [Space, Space],
            [
                new MnemonicBinding("w", "window.menu"),
                new MnemonicBinding("wc", "focus.chat"),
                new MnemonicBinding("wi", "focus.ide"),
                new MnemonicBinding("gc", "gc.open"),
            ]);

        return result.Table ?? throw new InvalidOperationException(string.Join("; ", result.Errors));
    }

    public static RouterContext Normal => new();

    public static RouterContext Focused => new(TextInputFocused: true, HasFocusableTextInput: true);

    public static RouterContext FocusableOnly => new(HasFocusableTextInput: true);
}
