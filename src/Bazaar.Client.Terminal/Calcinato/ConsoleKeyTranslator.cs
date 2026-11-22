using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Terminal.Calcinato;

/// <summary>
/// Turns console key presses into the <see cref="TerminalKey"/> values the modal engine reads. Named keys keep their
/// names, Ctrl+letter becomes a control byte (so Ctrl+C reaches the shell in Insert mode), and printable characters
/// become text. A high surrogate waits for its low half, so a character outside the BMP arrives whole.
/// </summary>
/// <remarks>Keep one instance per input stream; it holds a pending high surrogate between calls.</remarks>
public sealed class ConsoleKeyTranslator
{
    private const char _delete = '\u007F';
    private const char _lastControl = '\u001F';
    private const char _firstControl = '\u0001';

    private char _pendingHighSurrogate;

    /// <summary>Translates one key press.</summary>
    /// <param name="info">The console key press.</param>
    /// <returns>The key for the modal engine, or null when the press produces nothing (a lone surrogate half, or an unmapped key).</returns>
    public TerminalKey? Translate(ConsoleKeyInfo info)
    {
        bool shift = (info.Modifiers & ConsoleModifiers.Shift) != 0;
        bool control = (info.Modifiers & ConsoleModifiers.Control) != 0;

        KeyName named = NamedKey(info.Key);
        if (named != KeyName.None)
        {
            _pendingHighSurrogate = '\0';
            return TerminalKey.Named(named, shift);
        }

        if (info.Key == ConsoleKey.Spacebar && control)
        {
            _pendingHighSurrogate = '\0';
            return TerminalKey.Control(0x00);
        }

        if (control && info.Key >= ConsoleKey.A && info.Key <= ConsoleKey.Z && info.KeyChar == '\0')
        {
            _pendingHighSurrogate = '\0';
            return TerminalKey.Control((byte)(info.Key - ConsoleKey.A + 1));
        }

        char c = info.KeyChar;
        if (c == _delete)
        {
            _pendingHighSurrogate = '\0';
            return TerminalKey.Named(KeyName.Backspace);
        }

        if (c >= _firstControl && c <= _lastControl)
        {
            _pendingHighSurrogate = '\0';
            return TerminalKey.Control((byte)c);
        }

        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return null;
        }

        if (char.IsLowSurrogate(c))
        {
            if (_pendingHighSurrogate == '\0')
            {
                return null;
            }

            string pair = new([_pendingHighSurrogate, c]);
            _pendingHighSurrogate = '\0';
            return TerminalKey.Typed(pair);
        }

        _pendingHighSurrogate = '\0';
        if (c >= ' ')
        {
            return TerminalKey.Typed(c);
        }

        return null;
    }

    private static KeyName NamedKey(ConsoleKey key) => key switch
    {
        ConsoleKey.Enter => KeyName.Enter,
        ConsoleKey.Tab => KeyName.Tab,
        ConsoleKey.Backspace => KeyName.Backspace,
        ConsoleKey.Escape => KeyName.Escape,
        ConsoleKey.Insert => KeyName.Insert,
        ConsoleKey.Delete => KeyName.Delete,
        ConsoleKey.Home => KeyName.Home,
        ConsoleKey.End => KeyName.End,
        ConsoleKey.PageUp => KeyName.PageUp,
        ConsoleKey.PageDown => KeyName.PageDown,
        ConsoleKey.LeftArrow => KeyName.Left,
        ConsoleKey.RightArrow => KeyName.Right,
        ConsoleKey.UpArrow => KeyName.Up,
        ConsoleKey.DownArrow => KeyName.Down,
        ConsoleKey.F1 => KeyName.F1,
        ConsoleKey.F2 => KeyName.F2,
        ConsoleKey.F3 => KeyName.F3,
        ConsoleKey.F4 => KeyName.F4,
        ConsoleKey.F5 => KeyName.F5,
        ConsoleKey.F6 => KeyName.F6,
        ConsoleKey.F7 => KeyName.F7,
        ConsoleKey.F8 => KeyName.F8,
        ConsoleKey.F9 => KeyName.F9,
        ConsoleKey.F10 => KeyName.F10,
        ConsoleKey.F11 => KeyName.F11,
        ConsoleKey.F12 => KeyName.F12,
        _ => KeyName.None,
    };
}
