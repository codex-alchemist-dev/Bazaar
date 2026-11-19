using System.Buffers;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// An <see cref="ITokenAmountDigitScratch"/> in process memory, for digit streams that are known to be small and for tests.
/// Every chunk is held until the scratch is dropped, so a stream of any size belongs in a store-backed scratch instead.
/// </summary>
/// <remarks>Not thread-safe. Chunks are copied on write; each read returns a copy rented from the given pool.</remarks>
public sealed class InMemoryTokenAmountDigitScratch(TokenAmountBufferPool pool) : ITokenAmountDigitScratch
{
    private readonly Dictionary<(TokenAmountPart Part, long Seq), byte[]> _chunks = [];

    /// <summary>Chunks held.</summary>
    public int ChunkCount => _chunks.Count;

    /// <inheritdoc />
    public ValueTask WriteAsync(TokenAmountPart part, long seq, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _chunks[(part, seq)] = bytes.ToArray();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask FlushAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IMemoryOwner<byte>> ReadAsync(TokenAmountPart part, long seq, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_chunks.TryGetValue((part, seq), out var chunk))
        {
            throw new TokenAmountIntegrityException($"Scratch chunk {seq} of the {part} part is missing.");
        }

        var owner = pool.Rent(chunk.Length);
        chunk.CopyTo(owner.Memory);
        return ValueTask.FromResult(owner);
    }
}
