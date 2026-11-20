namespace Bazaar.Server.VmDisplay.Abstractions;

/// <summary>A pointer button.</summary>
public enum PointerButtonKind
{
    /// <summary>The primary button.</summary>
    Left,

    /// <summary>The middle button.</summary>
    Middle,

    /// <summary>The secondary button.</summary>
    Right,
}

/// <summary>Modifier keys held during a <see cref="KeyStroke"/>.</summary>
[Flags]
public enum KeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Shift.</summary>
    Shift = 1,

    /// <summary>Control.</summary>
    Control = 2,

    /// <summary>Alt.</summary>
    Alt = 4,

    /// <summary>The platform key (Windows or Super).</summary>
    Meta = 8,
}

/// <summary>One input event. Coordinates are guest pixels.</summary>
public abstract record InputAction;

/// <summary>Moves the pointer to an absolute position.</summary>
public sealed record PointerMove(int X, int Y) : InputAction;

/// <summary>Presses or releases a pointer button at the current position.</summary>
public sealed record PointerButton(PointerButtonKind Button, bool Down) : InputAction;

/// <summary>Moves to a position and clicks <paramref name="Count"/> times.</summary>
public sealed record PointerClick(int X, int Y, PointerButtonKind Button = PointerButtonKind.Left, int Count = 1) : InputAction;

/// <summary>Moves to a position and scrolls by the given deltas.</summary>
public sealed record PointerScroll(int X, int Y, int DeltaX, int DeltaY) : InputAction;

/// <summary>Presses and releases one key. <paramref name="Key"/> is a W3C UI Events <c>code</c> name from <see cref="DisplayKeys"/>.</summary>
public sealed record KeyStroke(string Key, KeyModifiers Modifiers = KeyModifiers.None) : InputAction;

/// <summary>Types text. The text may be sensitive, so it is never printed.</summary>
public sealed record TypeText(string Text) : InputAction
{
    /// <summary>Describes the action without its text.</summary>
    public override string ToString() => $"TypeText {{ {Text?.Length ?? 0} chars }}";
}

/// <summary>A batch of input for one target.</summary>
/// <param name="Target">The VM to drive.</param>
/// <param name="Actions">The actions, in order.</param>
/// <param name="Source">The only source to use, or <see langword="null"/> to let the module choose.</param>
public sealed record InputBatch(DisplayTarget Target, IReadOnlyList<InputAction> Actions, DisplaySourceKind? Source = null);

/// <summary>The fixed allowlist of key names (W3C UI Events <c>code</c> values) a <see cref="KeyStroke"/> may use.</summary>
public static class DisplayKeys
{
    private static readonly HashSet<string> _allowed = Build();

    /// <summary>Every allowed key name.</summary>
    public static IReadOnlyCollection<string> All => _allowed;

    /// <summary>Whether <paramref name="key"/> is on the allowlist (ordinal, case-sensitive).</summary>
    public static bool IsAllowed(string? key) => key is not null && _allowed.Contains(key);

    private static HashSet<string> Build()
    {
        var set = new HashSet<string>(StringComparer.Ordinal)
        {
            "Enter", "Tab", "Escape", "Backspace", "Delete", "Space",
            "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight",
            "Home", "End", "PageUp", "PageDown",
        };
        for (char c = 'A'; c <= 'Z'; c++)
            set.Add("Key" + c);
        for (int d = 0; d <= 9; d++)
            set.Add("Digit" + d);
        for (int f = 1; f <= 12; f++)
            set.Add("F" + f);
        return set;
    }
}
