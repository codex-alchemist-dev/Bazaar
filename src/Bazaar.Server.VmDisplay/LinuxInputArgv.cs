using System.Globalization;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay;

/// <summary>
/// Builds the fixed argv a Linux guest runs for input (docs/design/59, section 3). The tools are <c>wtype</c> for the keyboard and
/// <c>bazaar-pointer</c> (src/Bazaar.Guest.Pointer) for the pointer. No shell string is ever built: user text is one argv element
/// after <c>--</c>.
/// </summary>
/// <remarks>
/// <c>wlrctl</c> moves are relative and did not land at the requested position under headless cage (measured 2026-11-19,
/// docs/research/notes/desktop-density/measurements-2026-11-19.md), so the pointer goes through <c>bazaar-pointer</c>, which sends
/// absolute positions with the output size as the extent. Without a known output size the pointer is refused: a misplaced click
/// is worse than none.
/// </remarks>
internal static class LinuxInputArgv
{
    /// <summary>The reason pointer actions are refused on the guest source while the output size is unknown.</summary>
    public const string PointerRefusal = "Pointer input on the guest source needs the output size; capture the guest display first, or use the browser or hypervisor source.";

    // One scroll step per this many requested pixels (at least one), at most the tool's own bound per axis.
    private const int _pixelsPerScrollStep = 100;
    private const int _maxScrollSteps = 30;

    // Chromium drops the text of the first key of a wtype invocation when that key needs a keymap change (measured for "-" and
    // space; letters and digits survived). A leading Shift_L key tap absorbs the loss: 5 of 5 strings arrived whole, where a plain
    // call and a "-M shift -m shift" prefix each lost 2 of 5 (measured 2026-11-19,
    // docs/research/notes/desktop-density/measurements-2026-11-19.md).
    private static readonly string[] _warmUp = ["-k", "Shift_L"];

    private static readonly Dictionary<string, string> _namedKeys = new(StringComparer.Ordinal)
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
    /// <param name="actions">The actions.</param>
    /// <param name="extent">The output size in pixels that pointer coordinates refer to, or <see langword="null"/> when unknown.</param>
    /// <exception cref="DisplayException"><see cref="DisplayFailure.InvalidInput"/> for an action this tool set cannot express.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Build(IReadOnlyList<InputAction> actions, (int Width, int Height)? extent = null)
    {
        var commands = new List<IReadOnlyList<string>>();
        (int X, int Y)? at = null;
        foreach (InputAction action in actions)
        {
            switch (action)
            {
                case TypeText t:
                    commands.Add(["wtype", .. _warmUp, "--", t.Text]);
                    break;
                case KeyStroke k:
                    commands.Add(Key(k));
                    break;
                case PointerMove m:
                    at = (m.X, m.Y);
                    commands.Add(Pointer("move", m.X, m.Y, extent));
                    break;
                case PointerClick c:
                    at = (c.X, c.Y);
                    commands.Add(Pointer("click", c.X, c.Y, extent, ButtonName(c.Button), c.Count.ToString(CultureInfo.InvariantCulture)));
                    break;
                case PointerScroll s:
                    at = (s.X, s.Y);
                    commands.Add(Pointer("scroll", s.X, s.Y, extent, Steps(s.DeltaX), Steps(s.DeltaY)));
                    break;
                case PointerButton b:
                    // A button event acts at the current position, which the tool does not keep between runs.
                    if (at is not { } pos)
                        throw new DisplayException(DisplayFailure.InvalidInput, "A pointer button needs a pointer move, click or scroll before it in the same batch.");
                    commands.Add(Pointer(b.Down ? "down" : "up", pos.X, pos.Y, extent, ButtonName(b.Button)));
                    break;
                default:
                    throw new DisplayException(DisplayFailure.InvalidInput, "Unknown input action.");
            }
        }
        return commands;
    }

    private static List<string> Pointer(string verb, int x, int y, (int Width, int Height)? extent, params string[] rest)
    {
        if (extent is not { } size || size.Width < 1 || size.Height < 1)
            throw new DisplayException(DisplayFailure.InvalidInput, PointerRefusal);
        if (x >= size.Width || y >= size.Height)
            throw new DisplayException(DisplayFailure.InvalidInput, "The pointer position is outside the guest display.");
        return
        [
            "bazaar-pointer",
            verb,
            x.ToString(CultureInfo.InvariantCulture),
            y.ToString(CultureInfo.InvariantCulture),
            size.Width.ToString(CultureInfo.InvariantCulture),
            size.Height.ToString(CultureInfo.InvariantCulture),
            .. rest,
        ];
    }

    private static string ButtonName(PointerButtonKind button) => button switch
    {
        PointerButtonKind.Left => "left",
        PointerButtonKind.Right => "right",
        PointerButtonKind.Middle => "middle",
        _ => throw new DisplayException(DisplayFailure.InvalidInput, "Unknown pointer button."),
    };

    // Requested pixels to wheel steps: one per 100 pixels, at least one for any non-zero delta, signed, bounded.
    private static string Steps(int delta)
    {
        int steps = delta == 0 ? 0 : Math.Min(_maxScrollSteps, Math.Max(1, (Math.Abs(delta) + _pixelsPerScrollStep - 1) / _pixelsPerScrollStep));
        return (Math.Sign(delta) * steps).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The xkb key name <c>wtype -k</c> takes for an allowlisted key, or <see langword="null"/>.</summary>
    public static string? XkbName(string key)
    {
        if (_namedKeys.TryGetValue(key, out string? named))
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
            argv.AddRange(_warmUp);
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
