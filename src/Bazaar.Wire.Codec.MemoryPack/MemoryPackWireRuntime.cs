using System.Buffers;
using Bazaar.Contracts.Wire.Codec;
using global::MemoryPack;

namespace Bazaar.Wire.Codec.MemoryPack;

/// <summary>
/// Helpers the generated MemoryPack readers and writers call. Every decode limit lives here, so a generated reader cannot forget
/// one: depth, string length and collection length are checked before anything is allocated or read (doc 90 section 4.5), and every
/// object member must consume exactly the bytes its length announced.
/// </summary>
internal static class MemoryPackWireRuntime
{
    internal const int CollectionHeaderBytes = sizeof(int);

    public static void EnterContainer(int depth, MemoryPackLimits l)
    {
        if (depth > l.MaxDepth) throw new WireFormatException("Data nests deeper than the decode limit.");
    }

    /// <summary>Reads the varint length of each member of an object whose header was just read, and checks they fit in the data.</summary>
    public static void ReadMemberLengths(ref MemoryPackReader r, scoped Span<int> lengths)
    {
        long total = 0;
        for (var i = 0; i < lengths.Length; i++)
        {
            var length = r.ReadVarIntInt32();
            if (length < 0) throw new WireFormatException("An object member has a negative length.");
            lengths[i] = length;
            total += length;
        }

        if (total > r.Remaining) throw new WireFormatException("An object is longer than the data it sits in.");
    }

    /// <summary>A member must use exactly the bytes its length announced; otherwise the data is corrupt.</summary>
    public static void EndMember(ref MemoryPackReader r, int start, int announced)
    {
        if (r.Consumed - start != announced) throw new WireFormatException("An object member does not match its announced length.");
    }

    /// <summary>Skips the members a later minor version appended: the reader knows only <paramref name="known"/> of them.</summary>
    public static void SkipExtraMembers(ref MemoryPackReader r, scoped ReadOnlySpan<int> lengths, int known)
    {
        for (var i = known; i < lengths.Length; i++) r.Advance(lengths[i]);
    }

    /// <summary>
    /// Reads a collection header. Returns false for a null collection, which is only legal when <paramref name="nullable"/>.
    /// The count is checked against the item limit and against the bytes left (every element takes at least one byte) before
    /// the caller allocates anything.
    /// </summary>
    public static bool ReadCollectionStart(ref MemoryPackReader r, MemoryPackLimits l, int depth, bool nullable, out int count)
    {
        EnterContainer(depth, l);
        if (!r.TryReadCollectionHeader(out count))
        {
            if (!nullable) throw new WireFormatException("A required collection is null.");
            return false;
        }

        if (count < 0) throw new WireFormatException("A collection has a negative length.");
        if (count > l.MaxItems) throw new WireFormatException("A collection is longer than the decode limit.");
        if (count > r.Remaining) throw new WireFormatException("A collection announces more items than there are bytes.");
        return true;
    }

    public static string? ReadString(ref MemoryPackReader r, MemoryPackLimits l, bool nullable)
    {
        if (!r.TryPeekCollectionHeader(out var header))
        {
            // The null string header is -1.
            if (!nullable) throw new WireFormatException("A required string is null.");
            _ = r.ReadUnmanaged<int>();
            return null;
        }

        // A UTF-8 string announces the complement of its byte count (at most -2); a UTF-16 string announces its character count.
        // Only our own writer produces data here and it writes UTF-8; UTF-16 is accepted within the same cap, counting 3 bytes a character.
        var bytes = header < 0 ? (long)~header : (long)header * 3;
        if (bytes > l.MaxStringBytes) throw new WireFormatException("A string is longer than the decode limit.");
        return r.ReadString() ?? throw new WireFormatException("A string header disagrees with its content.");
    }

    public static bool ReadBool(ref MemoryPackReader r) => ReadFlag(ref r);

    /// <summary>One byte that must be 0 or 1; MemoryPack's raw bool read would accept any value.</summary>
    public static bool ReadFlag(ref MemoryPackReader r) => r.ReadUnmanaged<byte>() switch
    {
        0 => false,
        1 => true,
        _ => throw new WireFormatException("A flag byte is neither 0 nor 1."),
    };

    public static float[]? ReadFloat32Array(ref MemoryPackReader r, MemoryPackLimits l, bool nullable)
    {
        if (!r.TryPeekCollectionHeader(out var count))
        {
            if (!nullable) throw new WireFormatException("A required float array is null.");
            _ = r.ReadUnmanaged<int>();
            return null;
        }

        if (count < 0) throw new WireFormatException("A float array has a negative length.");
        if (count > l.MaxItems) throw new WireFormatException("A float array is longer than the decode limit.");
        if ((long)count * sizeof(float) + CollectionHeaderBytes > r.Remaining) throw new WireFormatException("A float array announces more floats than there are bytes.");
        return r.ReadUnmanagedArray<float>();
    }

    /// <summary>Writes the instant as UTC ticks plus the offset in minutes, so the offset survives and no invalid bit pattern can be read back.</summary>
    public static void WriteDateTimeOffset<TW>(ref MemoryPackWriter<TW> w, DateTimeOffset value)
        where TW : class, IBufferWriter<byte>
    {
        w.WriteUnmanaged(value.UtcTicks, (short)value.Offset.TotalMinutes);
    }

    public static DateTimeOffset ReadDateTimeOffset(ref MemoryPackReader r)
    {
        var utcTicks = r.ReadUnmanaged<long>();
        var minutes = r.ReadUnmanaged<short>();
        try
        {
            var offset = TimeSpan.FromMinutes(minutes);
            return new DateTimeOffset(utcTicks + offset.Ticks, offset);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new WireFormatException("A date-time is out of range.", ex);
        }
    }

    /// <summary>Writes the four parts of a decimal, so a malformed bit pattern can never become a decimal.</summary>
    public static void WriteDecimal<TW>(ref MemoryPackWriter<TW> w, decimal value)
        where TW : class, IBufferWriter<byte>
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        w.WriteUnmanaged(bits[0], bits[1], bits[2], bits[3]);
    }

    public static decimal ReadDecimal(ref MemoryPackReader r)
    {
        Span<int> bits = stackalloc int[4];
        r.ReadUnmanaged(out bits[0], out bits[1], out bits[2], out bits[3]);
        try
        {
            return new decimal(bits);
        }
        catch (ArgumentException ex)
        {
            throw new WireFormatException("A decimal is malformed.", ex);
        }
    }

    public static WireFormatException MissingField(string key) => new($"Payload has no '{key}'.");

    public static WireFormatException NullObject() => new("A required object is null.");

    public static WireFormatException UnknownEnum(string type, string value) => new($"{value} is not a known {type}.");
}
