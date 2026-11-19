using System.Buffers;

namespace Bazaar.Contracts.Numerics;

/// <summary>
/// A chunked token amount held in process memory (doc 48, DECIDED 11.4): first a <see cref="ITokenAmountChunkWriter"/> for
/// <see cref="TokenAmountChunkCodec"/>, then, once completed, an <see cref="ITokenAmountChunkReader"/> over what was written.
/// Used for small intermediate results and for in-memory operands such as a limit; large results belong in a store.
/// </summary>
/// <remarks>Not thread-safe while being written. Chunks are copied on write, so the codec may reuse its buffers.</remarks>
public sealed class InMemoryChunkedTokenAmount : ITokenAmountChunkReader, ITokenAmountChunkWriter
{
    private readonly Dictionary<(TokenAmountPart Part, long Seq), byte[]> _chunks = [];
    private byte[]? _sha256;
    private TokenAmountDigitCounts _digits;

    /// <summary>True once <see cref="CompleteAsync"/> has run.</summary>
    public bool IsComplete => _sha256 is not null;

    /// <summary>Chunks held.</summary>
    public int ChunkCount => _chunks.Count;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The amount is not complete yet.</exception>
    public TokenAmountDigitCounts Digits => IsComplete ? _digits : throw NotComplete();

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The amount is not complete yet.</exception>
    public ReadOnlyMemory<byte> Sha256 => _sha256 ?? throw NotComplete();

    /// <summary>The chunked form of an in-memory amount.</summary>
    public static async ValueTask<InMemoryChunkedTokenAmount> FromAsync(TokenAmount amount, TokenAmountChunkCodec codec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var chunked = new InMemoryChunkedTokenAmount();
        await codec.WriteAsync(amount, chunked, ct).ConfigureAwait(false);
        return chunked;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The amount is complete, or the chunk was written already.</exception>
    public ValueTask WriteChunkAsync(TokenAmountPart part, long seq, ReadOnlyMemory<byte> digits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsComplete)
        {
            throw new InvalidOperationException("The amount is complete; no chunk can be added.");
        }

        if (!_chunks.TryAdd((part, seq), digits.ToArray()))
        {
            throw new InvalidOperationException($"Chunk {seq} of the {part} part was written twice.");
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The amount is complete already.</exception>
    public ValueTask CompleteAsync(TokenAmountDigitCounts digits, ReadOnlyMemory<byte> sha256, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsComplete)
        {
            throw new InvalidOperationException("The amount is complete already.");
        }

        _digits = digits;
        _sha256 = sha256.ToArray();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IMemoryOwner<byte>> ReadChunkAsync(TokenAmountPart part, long seq, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsComplete)
        {
            throw NotComplete();
        }

        return _chunks.TryGetValue((part, seq), out var chunk)
            ? ValueTask.FromResult<IMemoryOwner<byte>>(new Borrowed(chunk))
            : throw new TokenAmountIntegrityException($"Chunk {seq} of the {part} part is missing.");
    }

    private static InvalidOperationException NotComplete() => new("The amount is still being written.");

    /// <summary>A view of a held chunk; disposing it frees nothing, because the amount keeps its chunks.</summary>
    private sealed class Borrowed(byte[] chunk) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => chunk;

        public void Dispose()
        {
        }
    }
}
