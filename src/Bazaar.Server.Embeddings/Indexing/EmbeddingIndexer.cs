namespace Bazaar.Server.Embeddings.Indexing;

/// <summary>What one sweep did. <see cref="Idle"/> is true when no profile is active (setting <c>none</c>), so nothing was read or sent.</summary>
/// <remarks><see cref="TooLong"/> counts blocks the model refused as too long; they are skipped (never truncated) and retried on the next sweep.</remarks>
public sealed record IndexRunResult(bool Idle, int Embedded, int SkippedPersonal, string? Error, int TooLong = 0)
{
    public static IndexRunResult IdleResult { get; } = new(true, 0, 0, null);
}

/// <summary>
/// One sweep over the Vault blocks that lack a current embedding under the active profile (doc 08, "WP6 detail"). Personal-data blocks
/// are skipped and never reach a provider. A provider failure ends the sweep with an error and writes nothing for that batch; the next
/// sweep retries it, and lexical search is unaffected. When a provider reports an input as too long, the batch is embedded block by block
/// so only the offending blocks are skipped.
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
        var tooLong = 0;
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
                    var dims = active.Profile.Dimensions;
                    var embeddedBlocks = new List<EmbeddedBlock>(eligible.Count);
                    try
                    {
                        var result = await active.Provider.EmbedAsync(eligible.Select(b => b.Body).ToArray(), cancellationToken).ConfigureAwait(false);
                        Check(result, eligible.Count, dims, active.Profile.ProfileId);
                        embeddedBlocks.AddRange(eligible.Select((b, i) => new EmbeddedBlock(b.DocId, b.Ordinal, b.TextHash, result.Vectors[i])));
                    }
                    catch (EmbeddingInputTooLongException)
                    {
                        // One block is over the model's cap. Find it by embedding the blocks one at a time and skip only that one.
                        foreach (var block in eligible)
                        {
                            try
                            {
                                var single = await active.Provider.EmbedAsync([block.Body], cancellationToken).ConfigureAwait(false);
                                Check(single, 1, dims, active.Profile.ProfileId);
                                embeddedBlocks.Add(new EmbeddedBlock(block.DocId, block.Ordinal, block.TextHash, single.Vectors[0]));
                            }
                            catch (EmbeddingInputTooLongException)
                            {
                                tooLong++;
                            }
                        }
                    }

                    if (embeddedBlocks.Count > 0)
                    {
                        embedded += await store.UpsertAsync(active.Profile.ProfileId, embeddedBlocks, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (EmbeddingException ex)
                {
                    return new IndexRunResult(Idle: false, embedded, skipped, ex.Message, tooLong);
                }
            }

            if (page.Count < batchSize)
            {
                break;
            }
        }

        return new IndexRunResult(Idle: false, embedded, skipped, null, tooLong);
    }

    private static void Check(EmbeddingResult result, int expected, int dims, Guid profileId)
    {
        if (result.Vectors.Count != expected || result.Vectors.Any(v => v.Length != dims))
        {
            throw new EmbeddingProfileMismatchException($"The provider returned vectors that do not fit profile {profileId} ({dims} dimensions).");
        }
    }
}
