using System.Globalization;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay;

/// <summary>
/// Builds the fixed argv a Linux guest runs for input (docs/design/59, section 3). The tool is <c>wtype</c> for the keyboard and
/// no pointer tool. No shell string is ever built: user text is one argv element after <c>--</c>.
/// </summary>
/// <remarks>
/// Pointer input is refused: <c>wlrctl</c> moves are relative and did not land at the requested position under headless cage
/// (measured 2026-11-19, docs/research/notes/desktop-density/measurements-2026-11-19.md). A misplaced click is worse than none.
/// </remarks>
internal static class LinuxInputArgv
{
    /// <summary>The reason pointer actions are refused on the guest source.</summary>
    public const string PointerRefusal = "Pointer input is not available on the guest source yet; use the browser or hypervisor source.";

    // Chromium drops the text of the first key of a wtype invocation when that key needs a keymap change (measured for "-" and
    // space; letters and digits survived). A leading Shift_L key tap absorbs the loss: 5 of 5 strings arrived whole, where a plain
    // call and a "-M shift -m shift" prefix each lost 2 of 5 (measured 2026-11-19,
    // docs/research/notes/desktop-density/measurements-2026-11-19.md).
    private static readonly string[] s_warmUp = ["-k", "Shift_L"];

    private static readonly Dictionary<string, string> s_namedKeys = new(StringComparer.Ordinal)
    {
        ["Enter"] = "Return",
        ["Tab"] = "Tab",
        ["Escape"] = "Escape",
        ["Backspace"] = "BackSpace",
        ["Delete"] = "Delete",
        ["Space"] = "space",
        ["ArrowUp"] = "Up",
        ["ArrowDown"] = "Down",
        ["ArrowLeft"] = "Left",
        ["ArrowRight"] = "Right",
        ["Home"] = "Home",
        ["End"] = "End",
        ["PageUp"] = "Prior",
        ["PageDown"] = "Next",
    };

    /// <summary>The argv lists to run, in order, for <paramref name="actions"/> (already validated).</summary>
    /// <exception cref="DisplayException"><see cref="DisplayFailure.InvalidInput"/> for an action this tool set cannot express.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Build(IReadOnlyList<InputAction> actions)
    {
        var commands = new List<IReadOnlyList<string>>();
        foreach (InputAction action in actions)
        {
            switch (action)
            {
                case TypeText t:
                    commands.Add(["wtype", .. s_warmUp, "--", t.Text]);
                    break;
                case KeyStroke k:
                    commands.Add(Key(k));
                    break;
                case PointerMove or PointerClick or PointerScroll or PointerButton:
                    throw new DisplayException(DisplayFailure.InvalidInput, PointerRefusal);
                default:
                    throw new DisplayException(DisplayFailure.InvalidInput, "Unknown input action.");
            }
        }
        return commands;
    }

    /// <summary>The xkb key name <c>wtype -k</c> takes for an allowlisted key, or <see langword="null"/>.</summary>
    public static string? XkbName(string key)
    {
        if (s_namedKeys.TryGetValue(key, out string? named))
            return named;
        if (key.Length == 4 && key.StartsWith("Key", StringComparison.Ordinal) && key[3] is >= 'A' and <= 'Z')
            return char.ToLowerInvariant(key[3]).ToString();
        if (key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal) && key[5] is >= '0' and <= '9')
            return key[5].ToString();
        if (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 12)
            return key;
        return null;
    }

    private static List<string> Key(KeyStroke k)
    {
        string name = XkbName(k.Key) ?? throw new DisplayException(DisplayFailure.InvalidInput, "The key has no xkb mapping.");
        var argv = new List<string> { "wtype" };
        List<string> mods = ModifierNames(k.Modifiers);
        if (mods.Count == 0)
            argv.AddRange(s_warmUp);
        foreach (string m in mods)
        {
            argv.Add("-M");
            argv.Add(m);
        }
        argv.Add("-k");
        argv.Add(name);
        foreach (string m in Enumerable.Reverse(mods))
        {
            argv.Add("-m");
            argv.Add(m);
        }
        return argv;
    }

    private static List<string> ModifierNames(KeyModifiers modifiers)
    {
        var names = new List<string>();
        if ((modifiers & KeyModifiers.Shift) != 0)
            names.Add("shift");
        if ((modifiers & KeyModifiers.Control) != 0)
            names.Add("ctrl");
        if ((modifiers & KeyModifiers.Alt) != 0)
            names.Add("alt");
        if ((modifiers & KeyModifiers.Meta) != 0)
            names.Add("logo");
        return names;
    }
}
