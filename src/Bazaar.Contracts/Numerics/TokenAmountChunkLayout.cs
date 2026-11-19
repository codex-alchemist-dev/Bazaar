namespace Bazaar.Contracts.Numerics;

/// <summary>
/// The packed layout of a large token amount (doc 48, DECIDED 11.4). Digits are packed two per byte as one base-100 value
/// (0 to 99), aligned at the decimal point: the integer part gets one leading zero pad digit when its length is odd, and the
/// fraction one trailing zero pad digit. Integer chunks are numbered from the point outward, fraction chunks from the point
/// onward. Every chunk holds exactly <see cref="ChunkBytes"/> bytes except the outermost chunk of each part, which holds the
/// remainder (1 to <see cref="ChunkBytes"/> bytes). Within a chunk the bytes are in reading order, most significant first.
/// </summary>
/// <remarks>
/// The chunk size is fixed at <see cref="StandardChunkBytes"/> (1 MiB, 2,097,152 digits); it is a layout choice, not a
/// setting. Smaller sizes exist only so tests can exercise multi-chunk carries with small numbers.
/// </remarks>
public sealed class TokenAmountChunkLayout
{
    /// <summary>The fixed chunk size: 1 MiB of packed bytes.</summary>
    public const int StandardChunkBytes = 1 << 20;

    /// <summary>The production layout.</summary>
    public static TokenAmountChunkLayout Standard { get; } = new(StandardChunkBytes);

    /// <summary>A layout with a smaller chunk size. Tests only.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkBytes"/> is not between 1 and <see cref="StandardChunkBytes"/>.</exception>
    internal TokenAmountChunkLayout(int chunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkBytes, StandardChunkBytes);
        ChunkBytes = chunkBytes;
    }

    /// <summary>Bytes in every chunk but the outermost of a part.</summary>
    public int ChunkBytes { get; }

    /// <summary>Packed bytes for a part of <paramref name="digits"/> digits: one byte per two digits, rounded up for the pad digit.</summary>
    public static long PackedBytes(long digits)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        return (digits / 2) + (digits & 1);
    }

    /// <summary>Chunks for a part of <paramref name="digits"/> digits. Zero digits have no chunk.</summary>
    public long ChunkCount(long digits)
    {
        var bytes = PackedBytes(digits);
        return (bytes / ChunkBytes) + (bytes % ChunkBytes == 0 ? 0 : 1);
    }

    /// <summary>The exact length of chunk <paramref name="seq"/> of a part of <paramref name="digits"/> digits.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="seq"/> is not a chunk of that part.</exception>
    public int ChunkLength(long digits, long seq)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seq);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(seq, ChunkCount(digits));
        return (int)Math.Min(ChunkBytes, PackedBytes(digits) - (seq * ChunkBytes));
    }
}
