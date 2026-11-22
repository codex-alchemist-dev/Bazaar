using System.Globalization;

namespace Bazaar.Guest.Pointer;

/// <summary>What the tool was asked to do.</summary>
internal enum PointerVerb
{
    Move,
    Click,
    Down,
    Up,
    Scroll,
}

/// <summary>A pointer button, with the Linux input code the protocol carries.</summary>
internal enum PointerButtonName
{
    Left,
    Right,
    Middle,
}

/// <summary>One validated command. <see cref="Width"/> and <see cref="Height"/> are the output size the coordinates refer to.</summary>
internal sealed record PointerCommand(
    PointerVerb Verb,
    int X,
    int Y,
    int Width,
    int Height,
    PointerButtonName Button = PointerButtonName.Left,
    int Count = 1,
    int DeltaX = 0,
    int DeltaY = 0)
{
    /// <summary>The Linux evdev code of <see cref="Button"/> (BTN_LEFT, BTN_RIGHT, BTN_MIDDLE).</summary>
    public uint ButtonCode => Button switch
    {
        PointerButtonName.Left => 0x110,
        PointerButtonName.Right => 0x111,
        _ => 0x112,
    };
}

/// <summary>
/// The strict argument parser: exact lower-case words, plain decimal integers only (digits, and a leading minus for scroll
/// steps), every value range-checked, nothing else accepted. There is no shell and no other input.
/// </summary>
internal static class PointerCommandParser
{
    /// <summary>The largest output edge, in pixels, the tool accepts.</summary>
    public const int MaxExtent = 8192;

    /// <summary>The most scroll steps per axis in one call.</summary>
    public const int MaxScrollSteps = 30;

    /// <summary>The most clicks in one call.</summary>
    public const int MaxClicks = 3;

    /// <summary>The usage line printed on a rejected command line.</summary>
    public const string Usage =
        "usage: bazaar-pointer move|down|up|click|scroll <x> <y> <width> <height> [button [count] | dx dy]";

    /// <summary>Parses <paramref name="args"/>.</summary>
    /// <returns><see langword="true"/> with <paramref name="command"/>, or <see langword="false"/> with a one-line <paramref name="error"/>.</returns>
    public static bool TryParse(string[] args, out PointerCommand? command, out string error)
    {
        command = null;
        error = "";
        if (args.Length == 0)
            return Fail(out error, "missing command");

        PointerVerb verb;
        int expected;
        switch (args[0])
        {
            case "move":
                verb = PointerVerb.Move;
                expected = 5;
                break;
            case "click":
                verb = PointerVerb.Click;
                expected = 6;
                break;
            case "down":
                verb = PointerVerb.Down;
                expected = 6;
                break;
            case "up":
                verb = PointerVerb.Up;
                expected = 6;
                break;
            case "scroll":
                verb = PointerVerb.Scroll;
                expected = 7;
                break;
            default:
                return Fail(out error, "unknown command");
        }

        // A click may carry an optional count.
        bool countGiven = verb == PointerVerb.Click && args.Length == 7;
        if (args.Length != expected && !countGiven)
            return Fail(out error, "wrong number of arguments");

        if (!Int(args[1], 0, MaxExtent - 1, out int x) || !Int(args[2], 0, MaxExtent - 1, out int y))
            return Fail(out error, "x and y must be whole numbers within the output");
        if (!Int(args[3], 1, MaxExtent, out int width) || !Int(args[4], 1, MaxExtent, out int height))
            return Fail(out error, $"width and height must be whole numbers from 1 to {MaxExtent}");
        if (x >= width || y >= height)
            return Fail(out error, "the point is outside the output");

        switch (verb)
        {
            case PointerVerb.Move:
                command = new PointerCommand(verb, x, y, width, height);
                return true;
            case PointerVerb.Scroll:
                if (!Int(args[5], -MaxScrollSteps, MaxScrollSteps, out int dx) || !Int(args[6], -MaxScrollSteps, MaxScrollSteps, out int dy))
                    return Fail(out error, $"scroll steps must be whole numbers from -{MaxScrollSteps} to {MaxScrollSteps}");
                if (dx == 0 && dy == 0)
                    return Fail(out error, "scroll steps are both zero");
                command = new PointerCommand(verb, x, y, width, height, DeltaX: dx, DeltaY: dy);
                return true;
            default:
                if (!TryButton(args[5], out PointerButtonName button))
                    return Fail(out error, "the button must be left, right or middle");
                int count = 1;
                if (countGiven && !Int(args[6], 1, MaxClicks, out count))
                    return Fail(out error, $"the click count must be a whole number from 1 to {MaxClicks}");
                command = new PointerCommand(verb, x, y, width, height, button, count);
                return true;
        }
    }

    private static bool TryButton(string text, out PointerButtonName button)
    {
        switch (text)
        {
            case "left":
                button = PointerButtonName.Left;
                return true;
            case "right":
                button = PointerButtonName.Right;
                return true;
            case "middle":
                button = PointerButtonName.Middle;
                return true;
            default:
                button = default;
                return false;
        }
    }

    // Digits only, with one leading minus when the range allows negatives: no plus, spaces, separators or exponents.
    private static bool Int(string text, int min, int max, out int value)
    {
        value = 0;
        if (text.Length is 0 or > 6)
            return false;
        int start = text[0] == '-' ? 1 : 0;
        if (start == 1 && min >= 0)
            return false;
        if (start == text.Length)
            return false;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] is < '0' or > '9')
                return false;
        }
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
    }

    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }
}
