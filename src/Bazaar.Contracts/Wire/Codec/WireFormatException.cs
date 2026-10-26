namespace Bazaar.Contracts.Wire.Codec;

/// <summary>A frame or payload could not be decoded. The connection layer closes with 4400 (bad frame).</summary>
public sealed class WireFormatException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public WireFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the decoder error that caused it.</summary>
    public WireFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}
