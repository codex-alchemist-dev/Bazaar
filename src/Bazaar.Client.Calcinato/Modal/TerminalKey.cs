namespace Bazaar.Client.Calcinato.Modal;

/// <summary>Named keys the modal engine reads. Only Shift is modelled as a modifier (doc 52, section 2: no Ctrl, Alt or Cmd chords).</summary>
public enum KeyName
{
    /// <summary>No named key. Used for text and raw control keys.</summary>
    None,
    Enter,
    Tab,
    Backspace,
    Escape,
    Insert,
    Delete,
    Home,
    End,
    PageUp,
    PageDown,
    Left,
    Right,
    Up,
    Down,
    F1,
    F2,
    F3,
    F4,
    F5,
    F6,
    F7,
    F8,
    F9,
    F10,
    F11,
    F12,
}

/// <summary>The kinds of key press the engine receives.</summary>
public enum TerminalKeyKind
{
    /// <summary>Printable text as committed by the keyboard layout. May hold more than one UTF-16 unit.</summary>
    Text,

    /// <summary>A named key such as Enter or PageUp, optionally with Shift.</summary>
    Named,

    /// <summary>A physical Ctrl-letter (or other control byte). Insert mode passes it to the shell; other modes ignore it.</summary>
    RawControl,
}

/// <summary>One key press as the modal engine sees it. The head builds it from its own key events.</summary>
/// <param name="Kind">Which of the three kinds this is.</param>
/// <param name="Text">Text for <see cref="TerminalKeyKind.Text"/>; empty otherwise.</param>
/// <param name="Name">Named key for <see cref="TerminalKeyKind.Named"/>; <see cref="KeyName.None"/> otherwise.</param>
/// <param name="Shift">Shift held (named keys only; text already carries its case).</param>
/// <param name="ControlByte">Control byte 0x00 to 0x1F for <see cref="TerminalKeyKind.RawControl"/>; 0 otherwise.</param>
public readonly record struct TerminalKey(TerminalKeyKind Kind, string Text, KeyName Name, bool Shift, byte ControlByte)
{
    /// <summary>A text key.</summary>
    public static TerminalKey Typed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new TerminalKey(TerminalKeyKind.Text, text, KeyName.None, false, 0);
    }

    /// <summary>A single text character.</summary>
    public static TerminalKey Typed(char c) => Typed(c.ToString());

    /// <summary>A named key.</summary>
    public static TerminalKey Named(KeyName name, bool shift = false)
    {
        if (name == KeyName.None)
        {
            throw new ArgumentException("A named key needs a name.", nameof(name));
        }

        return new TerminalKey(TerminalKeyKind.Named, string.Empty, name, shift, 0);
    }

    /// <summary>A physical control byte (0x00 to 0x1F) to pass to the shell in Insert mode.</summary>
    public static TerminalKey Control(byte value)
    {
        if (value > 0x1F)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Control bytes are 0x00 to 0x1F.");
        }

        return new TerminalKey(TerminalKeyKind.RawControl, string.Empty, KeyName.None, false, value);
    }
}
