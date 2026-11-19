using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Blobs;

/// <summary>Where a blob is attached: the GC and the seq of the entry.</summary>
/// <param name="GcId">GC holding the entry.</param>
/// <param name="Seq">Seq of the entry.</param>
public readonly record struct BlobGcLink(Guid GcId, long Seq);

/// <summary>Finds the GC entries that reference a blob.</summary>
public interface IBlobGcLinks
{
    /// <summary>Entries (<c>entry_ref</c> kind <c>blob</c>) naming <paramref name="hash"/>.</summary>
    ValueTask<IReadOnlyList<BlobGcLink>> FindAsync(string hash, CancellationToken ct);
}

/// <summary>Decides who may download a blob (doc 01 viewer rules; doc 02 <c>GET /blobs/{hash}</c>).</summary>
public interface IBlobReadAccess
{
    /// <summary>True when <paramref name="viewer"/> may read the blob.</summary>
    ValueTask<bool> CanReadAsync(string hash, GcPrincipal viewer, CancellationToken ct);
}
