namespace Bazaar.Server.Embeddings.Indexing;

/// <summary>A Vault block that has no embedding under the profile, or whose stored hash differs from its current text hash.</summary>
public sealed record PendingBlock(Guid DocId, int Ordinal, string Body, byte[] TextHash);

/// <summary>A computed embedding for one block, keyed by the hash of the text that was embedded.</summary>
public sealed record EmbeddedBlock(Guid DocId, int Ordinal, byte[] TextHash, float[] Vector);

/// <summary>Position in the keyset walk over <c>vault_blocks</c>; the next page starts after it.</summary>
public sealed record BlockCursor(Guid DocId, int Ordinal);

/// <summary>The indexer's view of <c>vault_blocks</c> and <c>vault_block_embedding</c> (doc 18).</summary>
public interface IEmbeddingIndexStore
{
    /// <summary>Blocks needing an embedding under <paramref name="profileId"/>, in (doc, ordinal) order, after <paramref name="after"/>.</summary>
    Task<IReadOnlyList<PendingBlock>> ListPendingAsync(Guid profileId, BlockCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces embeddings. Returns the number of rows written.</summary>
    Task<int> UpsertAsync(Guid profileId, IReadOnlyList<EmbeddedBlock> embeddings, CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether a block holds personal data, which is never embedded (doc 08 section 9). [PROPOSED] The default detector looks for the
/// encrypted block's fence; work package 12 owns the real format and replaces it.
/// </summary>
public interface IPersonalDataBlockDetector
{
    bool IsPersonalData(string body);
}

/// <summary>Detects a fenced block whose info string is <c>bazaar-encrypted</c>, with backtick or tilde fences.</summary>
public sealed class FencedPersonalDataBlockDetector : IPersonalDataBlockDetector
{
    public const string InfoString = "bazaar-encrypted";

    public bool IsPersonalData(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = body.AsSpan().TrimStart();
        foreach (var fence in (ReadOnlySpan<char>)['`', '~'])
        {
            var run = 0;
            while (run < text.Length && text[run] == fence)
            {
                run++;
            }

            if (run >= 3 && text[run..].TrimStart(' ').StartsWith(InfoString, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
