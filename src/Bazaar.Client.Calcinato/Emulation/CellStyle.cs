namespace Bazaar.Client.Calcinato.Emulation;

/// <summary>SGR text attributes. Stored only; drawing them is a head concern.</summary>
[Flags]
public enum CellAttributes : byte
{
    /// <summary>No attributes.</summary>
    None = 0,

    /// <summary>SGR 1.</summary>
    Bold = 1 << 0,

    /// <summary>SGR 2.</summary>
    Dim = 1 << 1,

    /// <summary>SGR 3.</summary>
    Italic = 1 << 2,

    /// <summary>SGR 4 (single underline).</summary>
    Underline = 1 << 3,

    /// <summary>SGR 7.</summary>
    Reverse = 1 << 4,

    /// <summary>SGR 9.</summary>
    Strikethrough = 1 << 5,

    /// <summary>SGR 8.</summary>
    Hidden = 1 << 6,
}

/// <summary>The SGR pen state a cell was written with.</summary>
/// <param name="Foreground">Foreground colour.</param>
/// <param name="Background">Background colour.</param>
/// <param name="Attributes">Text attributes.</param>
public readonly record struct CellStyle(TermColor Foreground, TermColor Background, CellAttributes Attributes)
{
    /// <summary>Default colours, no attributes.</summary>
    public static CellStyle Plain => default;
}
