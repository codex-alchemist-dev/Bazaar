using System.Text;

namespace Bazaar.Wire.Abstractions;

/// <summary>
/// The codec preface of stream transports (doc 90 section 4.2 Mode A): <c>0xBA 0x5A &lt;len&gt; &lt;codec names&gt;</c>,
/// where <c>len</c> is one byte and the names are ASCII, comma separated. The client sends its codecs in preference
/// order; the server answers with a preface holding the one chosen name. Reads are exact, so no frame byte is consumed.
/// </summary>
public static class WirePreface
{
    /// <summary>First magic byte.</summary>
    public const byte Magic0 = 0xBA;

    /// <summary>Second magic byte.</summary>
    public const byte Magic1 = 0x5A;

    /// <summary>Bytes in front of the names.</summary>
    public const int HeaderBytes = 3;

    /// <summary>Encodes a preface.</summary>
    /// <exception cref="ArgumentException">No names, a name that is empty, not printable ASCII or holds a comma, or more than 255 bytes.</exception>
    public static byte[] Encode(IReadOnlyList<string> codecs)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        if (codecs.Count == 0) throw new ArgumentException("At least one codec is needed.", nameof(codecs));
        foreach (var name in codecs)
        {
            if (string.IsNullOrEmpty(name) || !name.All(static c => c is > ' ' and < '\u007f' && c != ',')) throw new ArgumentException("Invalid codec name.", nameof(codecs));
        }

        var text = Encoding.ASCII.GetBytes(string.Join(',', codecs));
        if (text.Length > byte.MaxValue) throw new ArgumentException("Codec list too long.", nameof(codecs));
        var bytes = new byte[HeaderBytes + text.Length];
        bytes[0] = Magic0;
        bytes[1] = Magic1;
        bytes[2] = (byte)text.Length;
        text.CopyTo(bytes, HeaderBytes);
        return bytes;
    }

    /// <summary>Reads a preface from the stream, consuming exactly its bytes.</summary>
    /// <exception cref="InvalidDataException">Wrong magic, empty or malformed names.</exception>
    /// <exception cref="EndOfStreamException">The stream ended inside the preface.</exception>
    public static async ValueTask<IReadOnlyList<string>> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (header[0] != Magic0 || header[1] != Magic1) throw new InvalidDataException("Bad preface magic.");
        if (header[2] == 0) throw new InvalidDataException("Empty preface.");
        var text = new byte[header[2]];
        await stream.ReadExactlyAsync(text, cancellationToken).ConfigureAwait(false);
        if (text.Any(static b => b >= 0x7f)) throw new InvalidDataException("Preface is not ASCII.");
        var names = new List<string>();
        foreach (var part in Encoding.ASCII.GetString(text).Split(','))
        {
            if (part.Length == 0 || !part.All(static c => c is > ' ' and < '\u007f')) throw new InvalidDataException("Bad codec name in preface.");
            names.Add(part);
        }

        return names;
    }
}
