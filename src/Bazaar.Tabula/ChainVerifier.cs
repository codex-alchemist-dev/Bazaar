namespace Bazaar.Tabula;

/// <summary>
/// Standalone chain verifier (doc 85 R-16): needs only the entries, no database, no key and no host. Each named chain
/// verifies on its own.
/// </summary>
public static class ChainVerifier
{
    /// <summary>
    /// Checks one chain from genesis. When <paramref name="expectedHead"/> is given (from an anchor or signed export), a chain
    /// cut short at the tail fails even though its links hold.
    /// </summary>
    public static ChainVerification Verify(
        ChainId chain,
        IReadOnlyList<ChainEntry> entries,
        byte[]? expectedHead = null,
        string domain = ChainHasher.DefaultDomain) =>
        VerifyFrom(chain, entries, 1, ChainHasher.GenesisHash(), expectedHead, domain);

    /// <summary>
    /// Checks a run of entries that starts at <paramref name="startSeq"/> with <paramref name="startPrevHash"/>, for example the
    /// nearest remaining checkpoint after retention (doc 81 section 7.3).
    /// </summary>
    public static ChainVerification VerifyFrom(
        ChainId chain,
        IReadOnlyList<ChainEntry> entries,
        long startSeq,
        byte[] startPrevHash,
        byte[]? expectedHead = null,
        string domain = ChainHasher.DefaultDomain)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(startPrevHash);

        byte[] prev = startPrevHash;
        long expectedSeq = startSeq;
        for (int i = 0; i < entries.Count; i++)
        {
            ChainEntry entry = entries[i];
            if (entry.Chain != chain)
            {
                return ChainVerification.Fail(chain, i, entry.Seq, ChainFailure.WrongChain);
            }

            if (entry.Seq != expectedSeq)
            {
                return ChainVerification.Fail(chain, i, entry.Seq, ChainFailure.SequenceBroken);
            }

            if (!entry.PrevHash.AsSpan().SequenceEqual(prev))
            {
                return ChainVerification.Fail(chain, i, entry.Seq, ChainFailure.PrevHashMismatch);
            }

            if (!entry.Hash.AsSpan().SequenceEqual(ChainHasher.ComputeHash(entry, domain)))
            {
                return ChainVerification.Fail(chain, i, entry.Seq, ChainFailure.HashMismatch);
            }

            prev = entry.Hash;
            expectedSeq++;
        }

        if (expectedHead is not null && !prev.AsSpan().SequenceEqual(expectedHead))
        {
            return ChainVerification.Fail(chain, entries.Count, expectedSeq - 1, ChainFailure.HeadMismatch);
        }

        return ChainVerification.Ok(chain, entries.Count);
    }

    /// <summary>
    /// Splits a mixed export by chain (keeping input order) and verifies each chain from genesis. Chains named in
    /// <paramref name="expectedHeads"/> are also checked against that head.
    /// </summary>
    public static IReadOnlyDictionary<ChainId, ChainVerification> VerifyAll(
        IEnumerable<ChainEntry> entries,
        IReadOnlyDictionary<ChainId, byte[]>? expectedHeads = null,
        string domain = ChainHasher.DefaultDomain)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<ChainId, List<ChainEntry>> byChain = [];
        foreach (ChainEntry entry in entries)
        {
            if (!byChain.TryGetValue(entry.Chain, out List<ChainEntry>? list))
            {
                byChain[entry.Chain] = list = [];
            }

            list.Add(entry);
        }

        Dictionary<ChainId, ChainVerification> results = [];
        foreach ((ChainId chain, List<ChainEntry> list) in byChain)
        {
            byte[]? head = null;
            expectedHeads?.TryGetValue(chain, out head);
            results[chain] = Verify(chain, list, head, domain);
        }

        return results;
    }
}
