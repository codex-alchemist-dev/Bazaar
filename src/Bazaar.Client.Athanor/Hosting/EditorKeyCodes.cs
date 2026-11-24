using Bazaar.Client.Input;

namespace Bazaar.Client.Athanor.Hosting;

/// <summary>
/// The one place key names are converted (doc 57 §7). The editor page reports <c>KeyboardEvent.code</c> (<c>KeyM</c>,
/// <c>Digit1</c>, <c>ArrowUp</c>); the AvaloniaEdit host reports Avalonia <c>Key</c> names (<c>M</c>, <c>D1</c>, <c>Up</c>).
/// Both are accepted and turned into the code form for chords and into the router's <see cref="KeyStroke"/>.
/// </summary>
public static class EditorKeyCodes
{
    private static readonly Dictionary<string, string> _avaloniaToCode = new(StringComparer.Ordinal)
    {
        ["Return"] = "Enter",
        ["Back"] = "Backspace",
        ["Up"] = "ArrowUp",
        ["Down"] = "ArrowDown",
        ["Left"] = "ArrowLeft",
        ["Right"] = "ArrowRight",
        ["Prior"] = "PageUp",
        ["Next"] = "PageDown",
        ["Escape"] = "Escape",
        ["Enter"] = "Enter",
        ["Tab"] = "Tab",
        ["Delete"] = "Delete",
        ["Home"] = "Home",
        ["End"] = "End",
        ["PageUp"] = "PageUp",
        ["PageDown"] = "PageDown",
        ["Space"] = "Space",
    };

    private static readonly Dictionary<string, NamedKey> _named = new(StringComparer.Ordinal)
    {
        ["Escape"] = NamedKey.Escape,
        ["Enter"] = NamedKey.Enter,
        ["Backspace"] = NamedKey.Backspace,
        ["Tab"] = NamedKey.Tab,
        ["Delete"] = NamedKey.Delete,
        ["ArrowUp"] = NamedKey.Up,
        ["ArrowDown"] = NamedKey.Down,
        ["ArrowLeft"] = NamedKey.Left,
        ["ArrowRight"] = NamedKey.Right,
        ["Home"] = NamedKey.Home,
        ["End"] = NamedKey.End,
        ["PageUp"] = NamedKey.PageUp,
        ["PageDown"] = NamedKey.PageDown,
    };

    /// <summary>Every <c>KeyboardEvent.code</c> the router can act on, which are the chord candidates.</summary>
    public static IReadOnlyList<string> CandidateCodes { get; } = BuildCandidates();

    /// <summary>Normalises a key name from either scheme to its <c>KeyboardEvent.code</c>. False for a name this table does not know.</summary>
    public static bool TryNormalize(string? key, out string code)
    {
        code = string.Empty;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (key.Length == 4 && key.StartsWith("Key", StringComparison.Ordinal) && IsUpperLetter(key[3]))
        {
            code = key;
            return true;
        }

        if (key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(key[5]))
        {
            code = key;
            return true;
        }

        if (key.Length == 1 && IsUpperLetter(char.ToUpperInvariant(key[0])))
        {
            code = "Key" + char.ToUpperInvariant(key[0]);
            return true;
        }

        if (key.Length == 2 && key[0] == 'D' && char.IsAsciiDigit(key[1]))
        {
            code = "Digit" + key[1];
            return true;
        }

        if (IsFunctionKey(key))
        {
            code = key;
            return true;
        }

        if (_avaloniaToCode.TryGetValue(key, out var mapped))
        {
            code = mapped;
            return true;
        }

        if (_named.ContainsKey(key))
        {
            code = key;
            return true;
        }

        return false;
    }

    /// <summary>The chord text for a code and modifiers: <c>Alt+Meta+Shift+Code</c>. Ctrl is not a parameter, so no chord can contain it.</summary>
    public static string ToChord(string code, bool shift, bool alt, bool meta)
    {
        var parts = new List<string>(4);
        if (alt)
        {
            parts.Add("Alt");
        }

        if (meta)
        {
            parts.Add("Meta");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        parts.Add(code);
        return string.Join('+', parts);
    }

    /// <summary>The chord for a key event, or false when the key is unknown or the event carries Ctrl.</summary>
    public static bool TryToChord(EditorKeyEvent key, out string chord)
    {
        ArgumentNullException.ThrowIfNull(key);
        chord = string.Empty;
        if (key.Ctrl || !TryNormalize(key.Key, out var code))
        {
            return false;
        }

        chord = ToChord(code, key.Shift, key.Alt, key.Meta);
        return true;
    }

    /// <summary>The router's key stroke for a code and modifiers. False for keys with no stroke (F keys, punctuation, shifted digits).</summary>
    public static bool TryToKeyStroke(string code, bool shift, bool alt, bool meta, out KeyStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(code);
        stroke = default;
        var modifiers = KeyModifiers.None;
        if (shift)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if (alt)
        {
            modifiers |= KeyModifiers.Alt;
        }

        if (meta)
        {
            modifiers |= KeyModifiers.Meta;
        }

        if (_named.TryGetValue(code, out var named))
        {
            stroke = KeyStroke.OfKey(named, modifiers);
            return true;
        }

        if (code.Length == 4 && code.StartsWith("Key", StringComparison.Ordinal) && IsUpperLetter(code[3]))
        {
            stroke = KeyStroke.OfChar(shift ? code[3] : char.ToLowerInvariant(code[3]), modifiers);
            return true;
        }

        if (!shift && code.Length == 6 && code.StartsWith("Digit", StringComparison.Ordinal) && char.IsAsciiDigit(code[5]))
        {
            stroke = KeyStroke.OfChar(code[5], modifiers);
            return true;
        }

        if (code == "Space")
        {
            stroke = KeyStroke.OfChar(' ', modifiers);
            return true;
        }

        return false;
    }

    /// <summary>The router's key stroke for an editor key event. False for Ctrl events and unknown keys.</summary>
    public static bool TryToKeyStroke(EditorKeyEvent key, out KeyStroke stroke)
    {
        ArgumentNullException.ThrowIfNull(key);
        stroke = default;
        return !key.Ctrl
            && TryNormalize(key.Key, out var code)
            && TryToKeyStroke(code, key.Shift, key.Alt, key.Meta, out stroke);
    }

    private static bool IsUpperLetter(char c) => c is >= 'A' and <= 'Z';

    private static bool IsFunctionKey(string key) =>
        key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var n) && n is >= 1 and <= 12 && key[1] != '0';

    private static string[] BuildCandidates()
    {
        var list = new List<string>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            list.Add("Key" + c);
        }

        for (var d = 0; d <= 9; d++)
        {
            list.Add("Digit" + d);
        }

        list.Add("Space");
        list.AddRange(_named.Keys.OrderBy(k => k, StringComparer.Ordinal));
        return [.. list];
    }
}
