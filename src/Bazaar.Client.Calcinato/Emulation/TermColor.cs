namespace Bazaar.Client.Calcinato.Emulation;

/// <summary>Which kind of colour a <see cref="TermColor"/> holds.</summary>
public enum TermColorKind : byte
{
    /// <summary>The terminal's default foreground or background.</summary>
    Default = 0,

    /// <summary>A 256-colour palette index (0-15 are the 16 base colours).</summary>
    Indexed = 1,

    /// <summary>A 24-bit truecolour value.</summary>
    Rgb = 2,
}

/// <summary>A cell colour: the terminal default, a palette index, or RGB.</summary>
public readonly record struct TermColor
{
    private TermColor(TermColorKind kind, byte index, byte red, byte green, byte blue)
    {
        Kind = kind;
        Index = index;
        Red = red;
        Green = green;
        Blue = blue;
    }

    /// <summary>Which kind of colour this is.</summary>
    public TermColorKind Kind { get; }

    /// <summary>Palette index, valid when <see cref="Kind"/> is <see cref="TermColorKind.Indexed"/>.</summary>
    public byte Index { get; }

    /// <summary>Red component, valid when <see cref="Kind"/> is <see cref="TermColorKind.Rgb"/>.</summary>
    public byte Red { get; }

    /// <summary>Green component, valid when <see cref="Kind"/> is <see cref="TermColorKind.Rgb"/>.</summary>
    public byte Green { get; }

    /// <summary>Blue component, valid when <see cref="Kind"/> is <see cref="TermColorKind.Rgb"/>.</summary>
    public byte Blue { get; }

    /// <summary>The terminal default colour.</summary>
    public static TermColor Default => default;

    /// <summary>A palette colour.</summary>
    public static TermColor FromIndex(byte index) => new(TermColorKind.Indexed, index, 0, 0, 0);

    /// <summary>A truecolour value.</summary>
    public static TermColor FromRgb(byte red, byte green, byte blue) => new(TermColorKind.Rgb, 0, red, green, blue);
}
