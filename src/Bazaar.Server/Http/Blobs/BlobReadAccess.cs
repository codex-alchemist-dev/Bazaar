using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Read;
using Npgsql;

namespace Bazaar.Server.Http.Blobs;

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

/// <summary>No GC links: only the uploader reads. Used when the host has no database.</summary>
public sealed class NoBlobGcLinks : IBlobGcLinks
{
    /// <inheritdoc />
    public ValueTask<IReadOnlyList<BlobGcLink>> FindAsync(string hash, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<BlobGcLink>>([]);
}

/// <summary><see cref="IBlobGcLinks"/> over <c>entry_ref</c> (migration 0008).</summary>
public sealed class PgBlobGcLinks(NpgsqlDataSource dataSource) : IBlobGcLinks
{
    private const string _sql = "SELECT gc_id, seq FROM entry_ref WHERE ref_kind = 'blob' AND ref_id = @hash LIMIT 100";

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<BlobGcLink>> FindAsync(string hash, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.AddWithValue("hash", hash);
        var links = new List<BlobGcLink>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            links.Add(new BlobGcLink(reader.GetGuid(0), reader.GetInt64(1)));
        }

        return links;
    }
}

/// <summary>
/// Who may download a blob: the uploader, or anyone who can read a GC entry that references it (doc 01 viewer rules, through
/// <see cref="IGcReadAccess"/>). Without a read-access service only the uploader reads (fail closed).
/// </summary>
public sealed class BlobReadAccess(IBlobStore store, IBlobGcLinks links, IGcReadAccess? gcAccess)
{
    /// <summary>True when <paramref name="viewer"/> may read the blob.</summary>
    public async ValueTask<bool> CanReadAsync(string hash, GcPrincipal viewer, CancellationToken ct)
    {
        if (await store.IsUploaderAsync(hash, viewer.Id, ct).ConfigureAwait(false)) return true;
        if (gcAccess is null) return false;
        foreach (var link in await links.FindAsync(hash, ct).ConfigureAwait(false))
        {
            var grant = await gcAccess.GetGrantAsync(link.GcId, viewer, ct).ConfigureAwait(false);
            if (grant.Allowed && (grant.ThroughSeq is null || link.Seq <= grant.ThroughSeq)) return true;
        }

        return false;
    }
}
