using System.Buffers;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// A source of one stored large token amount in the chunked form (doc 48, DECIDED 11.4): its header and random access to its
/// chunks. Implementations are storage-specific (Postgres, memory); <see cref="TokenAmountChunkCodec"/> does the arithmetic.
/// </summary>
/// <remarks>
/// The codec reads one chunk at a time per operand and disposes each chunk before it asks for a chunk further along, so a
/// reader that rents its buffers from a <see cref="TokenAmountBufferPool"/> never holds more than one chunk per operand.
/// </remarks>
public interface ITokenAmountChunkReader
{
    /// <summary>The header digit counts, without pad digits.</summary>
    TokenAmountDigitCounts Digits { get; }

    /// <summary>
    /// The SHA-256 recorded for the packed digits (see <see cref="TokenAmountChunkCodec"/> for the order), or empty when the
    /// source records none. A full read checks it.
    /// </summary>
    ReadOnlyMemory<byte> Sha256 { get; }

    /// <summary>
    /// Returns chunk <paramref name="seq"/> of <paramref name="part"/>, with its stored length. The caller disposes it.
    /// </summary>
    /// <exception cref="TokenAmountIntegrityException">The chunk is missing.</exception>
    ValueTask<IMemoryOwner<byte>> ReadChunkAsync(TokenAmountPart part, long seq, CancellationToken ct);
}

/// <summary>
/// A sink for one new large token amount in the chunked form. <see cref="TokenAmountChunkCodec"/> writes every chunk once, in
/// the hash order (fraction chunks from the outermost inward, then integer chunks from the point outward), then completes
/// it with the true digit counts and the SHA-256 of the packed digits.
/// </summary>
/// <remarks>
/// The buffer passed to <see cref="WriteChunkAsync"/> belongs to the codec and is reused after the call returns, so an
/// implementation copies or sends it before returning.
/// </remarks>
public interface ITokenAmountChunkWriter
{
    /// <summary>Writes one chunk. Every chunk but the outermost of a part has exactly the layout's chunk size.</summary>
    ValueTask WriteChunkAsync(TokenAmountPart part, long seq, ReadOnlyMemory<byte> digits, CancellationToken ct);

    /// <summary>Records the header: the true digit counts and the SHA-256 of the packed digits. Called once, last.</summary>
    ValueTask CompleteAsync(TokenAmountDigitCounts digits, ReadOnlyMemory<byte> sha256, CancellationToken ct);
}
