using System.Text;

namespace Bazaar.Client.Calcinato.Emulation;

/// <summary>Receives parsed events from a <see cref="VtParser"/>.</summary>
public interface IVtSink
{
    /// <summary>A printable code point, already decoded from UTF-8.</summary>
    void Print(Rune rune);

    /// <summary>A C0 control byte (BEL, BS, HT, LF, VT, FF, CR, SO, SI and others).</summary>
    void Execute(byte control);

    /// <summary>A complete CSI sequence.</summary>
    void CsiDispatch(CsiSequence sequence);

    /// <summary>A complete ESC sequence that is not CSI, OSC or a string. <paramref name="intermediate"/> is 0 when absent.</summary>
    void EscDispatch(byte intermediate, byte final);

    /// <summary>A complete OSC payload (without the introducer and terminator). Length is capped by the parser.</summary>
    void OscDispatch(ReadOnlySpan<byte> payload);
}

/// <summary>A parsed CSI sequence. Missing and zero parameters read as 0 through <see cref="Raw"/>.</summary>
public readonly ref struct CsiSequence
{
    /// <summary>Creates a sequence view.</summary>
    /// <param name="parameters">Parameter values, at most <see cref="VtParser.MaxParameters"/>.</param>
    /// <param name="prefix">Private marker (<c>?</c>, <c>&gt;</c>, <c>&lt;</c>, <c>=</c>) or 0.</param>
    /// <param name="intermediate">First intermediate byte or 0.</param>
    /// <param name="final">Final byte.</param>
    public CsiSequence(ReadOnlySpan<int> parameters, byte prefix, byte intermediate, byte final)
    {
        Parameters = parameters;
        Prefix = prefix;
        Intermediate = intermediate;
        Final = final;
    }

    /// <summary>Parameter values in order.</summary>
    public ReadOnlySpan<int> Parameters { get; }

    /// <summary>Number of parameters present.</summary>
    public int Count => Parameters.Length;

    /// <summary>Private marker byte, or 0.</summary>
    public byte Prefix { get; }

    /// <summary>First intermediate byte, or 0.</summary>
    public byte Intermediate { get; }

    /// <summary>Final byte.</summary>
    public byte Final { get; }

    /// <summary>Parameter at <paramref name="index"/>, or <paramref name="defaultValue"/> when missing or zero.</summary>
    public int Get(int index, int defaultValue) => Raw(index) is var value && index < Count && value != 0 ? value : defaultValue;

    /// <summary>Parameter at <paramref name="index"/>, or 0 when missing.</summary>
    public int Raw(int index) => index >= 0 && index < Count ? Parameters[index] : 0;
}
