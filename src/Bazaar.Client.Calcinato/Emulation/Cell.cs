namespace Bazaar.Client.Calcinato.Emulation;

/// <summary>
/// One screen cell: a grapheme cluster (a base code point plus any combining marks), its display width and its style.
/// Width 2 is the first cell of a wide character. Width 0 is the trailing half of a wide character and carries no text.
/// </summary>
public readonly struct Cell
{
    private readonly string? _text;

    /// <summary>Creates a cell.</summary>
    /// <param name="text">Cluster text. Empty for a trailing half.</param>
    /// <param name="width">0 (trailing half), 1 or 2 columns.</param>
    /// <param name="style">Pen state when written.</param>
    public Cell(string text, byte width, CellStyle style)
    {
        _text = text;
        Width = width;
        Style = style;
    }

    /// <summary>Cluster text. A default-constructed cell reads as an empty trailing half.</summary>
    public string Text => _text ?? string.Empty;

    /// <summary>Display columns: 2 for a wide lead cell, 1 for a normal cell, 0 for the trailing half of a wide character.</summary>
    public byte Width { get; }

    /// <summary>Style the cell was written with.</summary>
    public CellStyle Style { get; }

    /// <summary>True for the trailing half of a wide character.</summary>
    public bool IsTrailingHalf => Width == 0;

    /// <summary>A blank cell in the given style.</summary>
    public static Cell Blank(CellStyle style) => new(" ", 1, style);
}
