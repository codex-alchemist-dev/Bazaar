namespace Bazaar.Server.Embeddings.Indexing;

/// <summary>What one sweep did. <see cref="Idle"/> is true when no profile is active (setting <c>none</c>), so nothing was read or sent.</summary>
public sealed record IndexRunResult(bool Idle, int Embedded, int SkippedPersonal, string? Error)
{
    public static IndexRunResult IdleResult { get; } = new(true, 0, 0, null);
}

/// <summary>
/// One sweep over the Vault blocks that lack a current embedding under the active profile (doc 08, "WP6 detail"). Personal-data blocks
/// are skipped and never reach a provider. A provider failure ends the sweep with an error and writes nothing for that batch; the next
/// sweep retries it, and lexical search is unaffected.
/// </summary>
public sealed class EmbeddingIndexer(
    IActiveEmbeddingSelector selector,
    IEmbeddingIndexStore store,
    IPersonalDataBlockDetector personalData,
    int batchSize = 32)
{
    public async Task<IndexRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        ActiveEmbedding? active;
        try
        {
            active = await selector.ResolveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (EmbeddingException ex)
        {
            return new IndexRunResult(Idle: false, 0, 0, ex.Message);
        }

        if (active is null)
        {
            return IndexRunResult.IdleResult;
        }

        var embedded = 0;
        var skipped = 0;
        BlockCursor? cursor = null;
        while (true)
        {
            var page = await store.ListPendingAsync(active.Profile.ProfileId, cursor, batchSize, cancellationToken).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            // The cursor moves past skipped blocks too, so a personal block never stalls the walk or gets re-listed.
            cursor = new BlockCursor(page[^1].DocId, page[^1].Ordinal);
            var eligible = new List<PendingBlock>(page.Count);
            foreach (var block in page)
            {
                if (personalData.IsPersonalData(block.Body))
                {
                    skipped++;
                }
                else
                {
                    eligible.Add(block);
                }
            }

            if (eligible.Count > 0)
            {
                try
                {
                    var result = await active.Provider.EmbedAsync(eligible.Select(b => b.Body).ToArray(), cancellationToken).ConfigureAwait(false);
                    var dims = active.Profile.Dimensions;
                    if (result.Vectors.Count != eligible.Count || result.Vectors.Any(v => v.Length != dims))
                    {
                        throw new EmbeddingProfileMismatchException(
                            $"The provider returned vectors that do not fit profile {active.Profile.ProfileId} ({dims} dimensions).");
                    }

                    var rows = eligible.Select((b, i) => new EmbeddedBlock(b.DocId, b.Ordinal, b.TextHash, result.Vectors[i])).ToArray();
                    embedded += await store.UpsertAsync(active.Profile.ProfileId, rows, cancellationToken).ConfigureAwait(false);
                }
                catch (EmbeddingException ex)
                {
                    return new IndexRunResult(Idle: false, embedded, skipped, ex.Message);
                }
            }

            if (page.Count < batchSize)
            {
                break;
            }
        }

        return new IndexRunResult(Idle: false, embedded, skipped, null);
    }
}
