using System.Text;

namespace Bazaar.Server.SignIn.WebAuthn.Cbor;

/// <summary>
/// Minimal, bounded CBOR reader for the two structures WebAuthn registration needs: the attestation object (a text-keyed map)
/// and the COSE public key (an integer-keyed map). Definite lengths only, no tags, no floats, no nesting beyond what the
/// caller asks for. Any malformed or oversized input throws <see cref="FormatException"/>.
/// </summary>
internal ref struct MiniCbor
{
    private readonly ReadOnlySpan<byte> _data;

    public MiniCbor(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public int Position { get; private set; }

    public readonly int Remaining => _data.Length - Position;

    public int ReadMapHeader(int maxEntries)
    {
        var (major, arg) = ReadHead();
        if (major != 5 || arg > (ulong)maxEntries)
        {
            throw new FormatException("Expected a small map.");
        }

        return (int)arg;
    }

    public string ReadText(int maxBytes)
    {
        var (major, arg) = ReadHead();
        if (major != 3 || arg > (ulong)maxBytes || arg > (ulong)Remaining)
        {
            throw new FormatException("Expected a text string.");
        }

        var s = Encoding.UTF8.GetString(_data.Slice(Position, (int)arg));
        Position += (int)arg;
        return s;
    }

    public byte[] ReadBytes(int maxBytes)
    {
        var (major, arg) = ReadHead();
        if (major != 2 || arg > (ulong)maxBytes || arg > (ulong)Remaining)
        {
            throw new FormatException("Expected a byte string.");
        }

        var b = _data.Slice(Position, (int)arg).ToArray();
        Position += (int)arg;
        return b;
    }

    /// <summary>Reads an unsigned or negative integer that fits in a <see cref="long"/>.</summary>
    public long ReadInt()
    {
        var (major, arg) = ReadHead();
        if (major > 1 || arg > long.MaxValue)
        {
            throw new FormatException("Expected an integer.");
        }

        return major == 0 ? (long)arg : -1 - (long)arg;
    }

    /// <summary>The major type of the next item without consuming it.</summary>
    public readonly int PeekMajor() => Remaining > 0 ? _data[Position] >> 5 : throw new FormatException("Truncated.");

    private (int Major, ulong Arg) ReadHead()
    {
        if (Remaining < 1)
        {
            throw new FormatException("Truncated.");
        }

        var initial = _data[Position++];
        var major = initial >> 5;
        var info = initial & 0x1F;
        if (info < 24)
        {
            return (major, (ulong)info);
        }

        var width = info switch { 24 => 1, 25 => 2, 26 => 4, 27 => 8, _ => throw new FormatException("Indefinite or reserved length.") };
        if (Remaining < width)
        {
            throw new FormatException("Truncated.");
        }

        ulong value = 0;
        for (var i = 0; i < width; i++)
        {
            value = (value << 8) | _data[Position++];
        }

        return (major, value);
    }
}
