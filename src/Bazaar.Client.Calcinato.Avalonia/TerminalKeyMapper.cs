using Avalonia.Input;
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Avalonia;

/// <summary>
/// Translates Avalonia key input into the <see cref="TerminalKey"/> the modal engine reads (doc 55, sections 5.2 and 5.4).
/// Named keys come from key-down events. Text comes from text-input events, which carry the layout's committed text and
/// IME results, so the head calls <see cref="FromText"/> for those.
/// </summary>
/// <remarks>
/// Calcinato defines no Ctrl chords of its own. A physical Ctrl with a letter becomes <see cref="TerminalKey.Control"/> with
/// the control byte, so Insert mode passes it to the shell (Ctrl-C is byte 3). The engine ignores control bytes in the other modes.
/// </remarks>
public static class TerminalKeyMapper
{
    /// <summary>
    /// Maps a key-down. Returns false for keys that produce text (letters, digits, punctuation), which arrive through
    /// <see cref="FromText"/> instead, and for keys the terminal does not use.
    /// </summary>
    /// <param name="key">The key that went down.</param>
    /// <param name="modifiers">Modifiers held with it.</param>
    /// <param name="terminalKey">The mapped key when the result is true.</param>
    public static bool TryMapKey(Key key, KeyModifiers modifiers, out TerminalKey terminalKey)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        bool control = modifiers.HasFlag(KeyModifiers.Control);

        if (control && key >= Key.A && key <= Key.Z)
        {
            terminalKey = TerminalKey.Control((byte)((int)key - (int)Key.A + 1));
            return true;
        }

        if (control && key == Key.Space)
        {
            terminalKey = TerminalKey.Control(0);
            return true;
        }

        KeyName name = NamedKey(key);
        if (name == KeyName.None)
        {
            terminalKey = default;
            return false;
        }

        terminalKey = TerminalKey.Named(name, shift);
        return true;
    }

    /// <summary>
    /// Maps committed text (from a text-input event, including IME results). Returns false for empty text and for text
    /// made of control characters, which the key-down path already handles (Enter, Tab, Backspace).
    /// </summary>
    /// <param name="text">The committed text.</param>
    /// <param name="terminalKey">The text key when the result is true.</param>
    public static bool FromText(string? text, out TerminalKey terminalKey)
    {
        if (string.IsNullOrEmpty(text) || text.Any(c => c < 0x20 || c == 0x7F))
        {
            terminalKey = default;
            return false;
        }

        terminalKey = TerminalKey.Typed(text);
        return true;
    }

    private static KeyName NamedKey(Key key) => key switch
    {
        Key.Enter => KeyName.Enter,
        Key.Tab => KeyName.Tab,
        Key.Back => KeyName.Backspace,
        Key.Escape => KeyName.Escape,
        Key.Insert => KeyName.Insert,
        Key.Delete => KeyName.Delete,
        Key.Home => KeyName.Home,
        Key.End => KeyName.End,
        Key.PageUp => KeyName.PageUp,
        Key.PageDown => KeyName.PageDown,
        Key.Left => KeyName.Left,
        Key.Right => KeyName.Right,
        Key.Up => KeyName.Up,
        Key.Down => KeyName.Down,
        Key.F1 => KeyName.F1,
        Key.F2 => KeyName.F2,
        Key.F3 => KeyName.F3,
        Key.F4 => KeyName.F4,
        Key.F5 => KeyName.F5,
        Key.F6 => KeyName.F6,
        Key.F7 => KeyName.F7,
        Key.F8 => KeyName.F8,
        Key.F9 => KeyName.F9,
        Key.F10 => KeyName.F10,
        Key.F11 => KeyName.F11,
        Key.F12 => KeyName.F12,
        _ => KeyName.None,
    };
}
