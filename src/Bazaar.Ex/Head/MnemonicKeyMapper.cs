using Avalonia.Input;
using Bazaar.Client.Core.Input;
using AvaloniaKey = Avalonia.Input.Key;
using CoreModifiers = Bazaar.Client.Core.Input.KeyModifiers;
using AvaloniaModifiers = Avalonia.Input.KeyModifiers;

namespace Bazaar.Ex.Head;

/// <summary>Maps Avalonia key events to the head-neutral <see cref="KeyStroke"/> the router reads. Keys it cannot map are left alone.</summary>
public static class MnemonicKeyMapper
{
    public static bool TryMap(AvaloniaKey key, string? keySymbol, AvaloniaModifiers modifiers, out KeyStroke stroke)
    {
        stroke = default;
        var mapped = MapModifiers(modifiers);

        var named = MapNamed(key);
        if (named is not NamedKey.None)
        {
            stroke = KeyStroke.OfKey(named, mapped);
            return true;
        }

        // A printable symbol carries the shifted character (Shift+W is "W"). Fall back to the key for letters and digits.
        if (keySymbol is { Length: 1 } symbol && !char.IsControl(symbol[0]))
        {
            stroke = KeyStroke.OfChar(symbol[0], mapped);
            return true;
        }

        if (TryMapCharacter(key, mapped, out var character))
        {
            stroke = KeyStroke.OfChar(character, mapped);
            return true;
        }

        return false;
    }

    private static NamedKey MapNamed(AvaloniaKey key) => key switch
    {
        AvaloniaKey.Escape => NamedKey.Escape,
        AvaloniaKey.Enter => NamedKey.Enter,
        AvaloniaKey.Back => NamedKey.Backspace,
        AvaloniaKey.Tab => NamedKey.Tab,
        AvaloniaKey.Delete => NamedKey.Delete,
        AvaloniaKey.Up => NamedKey.Up,
        AvaloniaKey.Down => NamedKey.Down,
        AvaloniaKey.Left => NamedKey.Left,
        AvaloniaKey.Right => NamedKey.Right,
        AvaloniaKey.Home => NamedKey.Home,
        AvaloniaKey.End => NamedKey.End,
        AvaloniaKey.PageUp => NamedKey.PageUp,
        AvaloniaKey.PageDown => NamedKey.PageDown,
        _ => NamedKey.None,
    };

    private static bool TryMapCharacter(AvaloniaKey pressed, CoreModifiers modifiers, out char character)
    {
        character = default;
        var shift = (modifiers & CoreModifiers.Shift) != 0;
        if (IsInRange(pressed, AvaloniaKey.A, AvaloniaKey.Z))
        {
            var offset = (int)pressed - (int)AvaloniaKey.A;
            character = (char)((shift ? 'A' : 'a') + offset);
            return true;
        }

        if (IsInRange(pressed, AvaloniaKey.D0, AvaloniaKey.D9))
        {
            character = (char)('0' + ((int)pressed - (int)AvaloniaKey.D0));
            return true;
        }

        if (pressed == AvaloniaKey.Space)
        {
            character = ' ';
            return true;
        }

        return false;
    }

    /// <summary>Inclusive range check on the enum's numeric order. Written as a helper so no comparison operator sits beside the key identifier.</summary>
    private static bool IsInRange(AvaloniaKey pressed, AvaloniaKey first, AvaloniaKey last) =>
        (int)pressed >= (int)first && (int)pressed <= (int)last;

    private static CoreModifiers MapModifiers(AvaloniaModifiers modifiers)
    {
        var result = CoreModifiers.None;
        if (modifiers.HasFlag(AvaloniaModifiers.Shift))
        {
            result |= CoreModifiers.Shift;
        }

        if (modifiers.HasFlag(AvaloniaModifiers.Control))
        {
            result |= CoreModifiers.Control;
        }

        if (modifiers.HasFlag(AvaloniaModifiers.Alt))
        {
            result |= CoreModifiers.Alt;
        }

        if (modifiers.HasFlag(AvaloniaModifiers.Meta))
        {
            result |= CoreModifiers.Meta;
        }

        return result;
    }
}
