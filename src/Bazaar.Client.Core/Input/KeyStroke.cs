namespace Bazaar.Client.Core.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Meta = 8,
}

public enum NamedKey
{
    None = 0,
    Escape,
    Enter,
    Backspace,
    Tab,
    Delete,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
}

/// <summary>
/// One key press as the head reports it. Printable keys carry the character already
/// shifted (Shift+W is 'W'); other keys carry a <see cref="NamedKey"/>. No Avalonia types.
/// </summary>
public readonly record struct KeyStroke(char? Character, NamedKey Named, KeyModifiers Modifiers)
{
    public static KeyStroke OfChar(char character, KeyModifiers modifiers = KeyModifiers.None) =>
        new(character, NamedKey.None, modifiers);

    public static KeyStroke OfKey(NamedKey named, KeyModifiers modifiers = KeyModifiers.None) =>
        new(null, named, modifiers);

    public bool HasChordModifier =>
        (Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0;
}
