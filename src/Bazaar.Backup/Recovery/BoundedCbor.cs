using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Backup.Recovery;

/// <summary>
/// Minimal CBOR reader for COSE keys: one top-level map with integer keys and scalar values (integers, byte and text
/// strings). Bounded size and item count, definite lengths only; nested containers are refused outright.
/// </summary>
internal static class BoundedCbor
{
    public const int MaxBytes = 1024;
    public const int MaxItems = 16;

    internal sealed record Value(long? Int, byte[]? Bytes, string? Text);

    /// <summary>Returns null if malformed, oversized, indefinite, nested, duplicated, or followed by trailing bytes.</summary>
    public static Dictionary<long, Value>? ReadIntKeyedMap(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxBytes)
        {
            return null;
        }

        var pos = 0;
        if (!ReadHead(data, ref pos, out var major, out var arg, out var indefinite) || indefinite || major != 5 || arg > MaxItems)
        {
            return null;
        }

        var map = new Dictionary<long, Value>();
        for (ulong i = 0; i < arg; i++)
        {
            var key = ReadScalar(data, ref pos);
            if (key?.Int is not { } k || map.ContainsKey(k))
            {
                return null;
            }

            var val = ReadScalar(data, ref pos);
            if (val is null)
            {
                return null;
            }

            map[k] = val;
        }

        return pos == data.Length ? map : null;
    }

    private static Value? ReadScalar(ReadOnlySpan<byte> data, ref int pos)
    {
        if (!ReadHead(data, ref pos, out var major, out var arg, out var indefinite) || indefinite)
        {
            return null;
        }

        switch (major)
        {
            case 0:
                return arg <= long.MaxValue ? new Value((long)arg, null, null) : null;
            case 1:
                return arg <= long.MaxValue ? new Value(-1 - (long)arg, null, null) : null;
            case 2:
            case 3:
                if (arg > (ulong)(data.Length - pos))
                {
                    return null;
                }

                var slice = data.Slice(pos, (int)arg);
                pos += (int)arg;
                if (major == 2)
                {
                    return new Value(null, slice.ToArray(), null);
                }

                try
                {
                    return new Value(null, null, new UTF8Encoding(false, true).GetString(slice));
                }
                catch (ArgumentException)
                {
                    return null;
                }

            default:
                return null;
        }
    }

    private static bool ReadHead(ReadOnlySpan<byte> data, ref int pos, out int major, out ulong arg, out bool indefinite)
    {
        major = 0;
        arg = 0;
        indefinite = false;
        if (pos >= data.Length)
        {
            return false;
        }

        var b = data[pos++];
        major = b >> 5;
        var info = b & 0x1F;
        switch (info)
        {
            case < 24:
                arg = (ulong)info;
                return true;
            case 24:
                if (pos + 1 > data.Length)
                {
                    return false;
                }

                arg = data[pos++];
                return true;
            case 25:
                if (pos + 2 > data.Length)
                {
                    return false;
                }

                arg = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
                pos += 2;
                return true;
            case 26:
                if (pos + 4 > data.Length)
                {
                    return false;
                }

                arg = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                pos += 4;
                return true;
            case 27:
                if (pos + 8 > data.Length)
                {
                    return false;
                }

                arg = BinaryPrimitives.ReadUInt64BigEndian(data[pos..]);
                pos += 8;
                return true;
            case 31:
                indefinite = true;
                return true;
            default:
                return false;
        }
    }
}
