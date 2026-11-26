using System.Text;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;

namespace Bazaar.Client.Tui.TerminalGui;

/// <summary>
/// Maps a Terminal.Gui key to the port's <see cref="TerminalKey"/>, so the shell can drive the same <see cref="MenuBarState"/> the
/// console path uses. No Ctrl chord is bound to anything except cancel and end-of-input (doc 58 section 4); every other chord, and
/// every Alt chord, is ignored.
/// </summary>
internal static class KeyTranslator
{
    public static TerminalKey? Translate(Key key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.IsCtrl)
        {
            return key.NoCtrl.KeyCode switch
            {
                KeyCode.C => new TerminalKey(TerminalKeyKind.Cancel),
                KeyCode.D => new TerminalKey(TerminalKeyKind.EndOfInput),
                _ => null,
            };
        }

        if (key.IsAlt)
        {
            return null;
        }

        var plain = key.KeyCode & ~KeyCode.ShiftMask;
        switch (plain)
        {
            case KeyCode.CursorUp:
                return new TerminalKey(TerminalKeyKind.Up);
            case KeyCode.CursorDown:
                return new TerminalKey(TerminalKeyKind.Down);
            case KeyCode.CursorLeft:
                return new TerminalKey(TerminalKeyKind.Left);
            case KeyCode.CursorRight:
                return new TerminalKey(TerminalKeyKind.Right);
            case KeyCode.Enter:
                return new TerminalKey(TerminalKeyKind.Enter);
            case KeyCode.Esc:
                return new TerminalKey(TerminalKeyKind.Escape);
            case KeyCode.Backspace:
                return new TerminalKey(TerminalKeyKind.Backspace);
            case KeyCode.Tab:
                return new TerminalKey(TerminalKeyKind.Tab);
        }

        if (key.TryGetPrintableRune(out Rune rune) && rune.IsBmp && !char.IsControl((char)rune.Value))
        {
            return TerminalKey.Of((char)rune.Value);
        }

        return null;
    }
}
