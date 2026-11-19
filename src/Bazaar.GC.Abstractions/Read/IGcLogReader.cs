using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Gc.Read;

/// <summary>Reads the GC log (docs/design/01, <c>ReadAsync</c>).</summary>
public interface IGcLogReader
{
    /// <summary>Largest page <see cref="ReadAsync"/> accepts.</summary>
    public const int MaxLimit = 10_000;

    /// <summary>
    /// Events with seq greater than <paramref name="afterSeq"/>, in seq order, at most <paramref name="limit"/> of them and none
    /// past the viewer's grant. Throws <see cref="GcAccessDeniedException"/> when the viewer may not read the GC. Because writers
    /// hold a per-GC lock to commit, committed seqs always form a contiguous prefix: a page never skips an event that commits later.
    /// </summary>
    IAsyncEnumerable<GcEvent> ReadAsync(Guid gcId, long afterSeq, int limit, GcPrincipal viewer, CancellationToken ct);

    /// <summary>The GC's <c>head_seq</c>, the last assigned seq (0 for an empty GC). Used by sweeps; no viewer rules apply to a number.</summary>
    Task<long> GetHeadSeqAsync(Guid gcId, CancellationToken ct);
}
