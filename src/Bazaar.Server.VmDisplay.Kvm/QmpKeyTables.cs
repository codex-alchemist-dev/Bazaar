using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>Maps the allowlisted W3C key names and printable US-ASCII text to QEMU qcodes (US keyboard layout).</summary>
internal static class QmpKeyTables
{
    private static readonly Dictionary<string, string> s_keys = BuildKeys();
    private static readonly Dictionary<char, (string QCode, bool Shift)> s_chars = BuildChars();

    /// <summary>Whether <paramref name="key"/> has a qcode, and which.</summary>
    public static bool TryGetKey(string key, out string qcode)
    {
        bool found = s_keys.TryGetValue(key, out string? q);
        qcode = q ?? string.Empty;
        return found;
    }

    /// <summary>The qcode and shift state that types <paramref name="c"/> on a US layout. Only printable ASCII, newline and tab map.</summary>
    public static bool TryGetChar(char c, out string qcode, out bool shift)
    {
        bool found = s_chars.TryGetValue(c, out (string QCode, bool Shift) v);
        qcode = v.QCode ?? string.Empty;
        shift = v.Shift;
        return found;
    }

    /// <summary>The qcode of a modifier, in the order they are pressed.</summary>
    public static IEnumerable<string> ModifierQCodes(KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift))
            yield return "shift";
        if (modifiers.HasFlag(KeyModifiers.Control))
            yield return "ctrl";
        if (modifiers.HasFlag(KeyModifiers.Alt))
            yield return "alt";
        if (modifiers.HasFlag(KeyModifiers.Meta))
            yield return "meta_l";
    }

    private static Dictionary<string, string> BuildKeys()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Enter"] = "ret",
            ["Tab"] = "tab",
            ["Escape"] = "esc",
            ["Backspace"] = "backspace",
            ["Delete"] = "delete",
            ["Space"] = "spc",
            ["ArrowUp"] = "up",
            ["ArrowDown"] = "down",
            ["ArrowLeft"] = "left",
            ["ArrowRight"] = "right",
            ["Home"] = "home",
            ["End"] = "end",
            ["PageUp"] = "pgup",
            ["PageDown"] = "pgdn",
        };
        for (char c = 'A'; c <= 'Z'; c++)
            map["Key" + c] = char.ToLowerInvariant(c).ToString();
        for (int d = 0; d <= 9; d++)
            map["Digit" + d] = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (int f = 1; f <= 12; f++)
            map["F" + f.ToString(System.Globalization.CultureInfo.InvariantCulture)] = "f" + f.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return map;
    }

    private static Dictionary<char, (string, bool)> BuildChars()
    {
        var map = new Dictionary<char, (string, bool)>
        {
            [' '] = ("spc", false),
            ['\n'] = ("ret", false),
            ['\t'] = ("tab", false),
        };
        for (char c = 'a'; c <= 'z'; c++)
        {
            map[c] = (c.ToString(), false);
            map[char.ToUpperInvariant(c)] = (c.ToString(), true);
        }

        for (char d = '0'; d <= '9'; d++)
            map[d] = (d.ToString(), false);

        // Unshifted punctuation, then the shifted form on the same key.
        (char Plain, char Shifted, string QCode)[] punctuation =
        [
            ('-', '_', "minus"),
            ('=', '+', "equal"),
            ('[', '{', "bracket_left"),
            (']', '}', "bracket_right"),
            ('\\', '|', "backslash"),
            (';', ':', "semicolon"),
            ('\'', '"', "apostrophe"),
            (',', '<', "comma"),
            ('.', '>', "dot"),
            ('/', '?', "slash"),
            ('`', '~', "grave_accent"),
        ];
        foreach ((char plain, char shifted, string qcode) in punctuation)
        {
            map[plain] = (qcode, false);
            map[shifted] = (qcode, true);
        }

        // Shifted digits.
        const string shiftedDigits = ")!@#$%^&*(";
        for (int i = 0; i < shiftedDigits.Length; i++)
            map[shiftedDigits[i]] = (i.ToString(System.Globalization.CultureInfo.InvariantCulture), true);

        return map;
    }
}
