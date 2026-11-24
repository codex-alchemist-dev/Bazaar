using Npgsql;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// Postgres <see cref="IForkLinkStore"/> over <c>fork_link</c> (time_machine migration 0001). Append-only: it only inserts and
/// selects, and the unique key (source_gc_id, source_seq, request_id) makes a repeat return the first row.
/// </summary>
public sealed class PostgresForkLinkStore(NpgsqlDataSource dataSource) : IForkLinkStore
{
    private const string _columns = "new_gc_id, source_gc_id, source_seq, vault_commit, request_id, created_at";
    private const string _insert =
        "INSERT INTO fork_link (" + _columns + ") VALUES ($1, $2, $3, $4, $5, $6) " +
        "ON CONFLICT (source_gc_id, source_seq, request_id) DO NOTHING";
    private const string _find =
        "SELECT " + _columns + " FROM fork_link WHERE source_gc_id = $1 AND source_seq = $2 AND request_id = $3";
    private const string _list = "SELECT " + _columns + " FROM fork_link WHERE source_gc_id = $1 ORDER BY created_at, new_gc_id";

    /// <inheritdoc />
    public async Task<ForkLink?> FindAsync(Guid sourceGcId, long sourceSeq, Guid requestId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_find);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sourceGcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = sourceSeq });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requestId });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? Read(r) : null;
    }

    /// <inheritdoc />
    public async Task<(ForkLink Link, bool Created)> AppendAsync(ForkLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        await using var cmd = dataSource.CreateCommand(_insert);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = link.NewGcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = link.SourceGcId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = link.SourceSeq });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = link.VaultCommit });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = link.RequestId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = link.CreatedAt.ToUniversalTime() });
        var inserted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (inserted == 1)
            return (link, true);

        var existing = await FindAsync(link.SourceGcId, link.SourceSeq, link.RequestId, ct).ConfigureAwait(false);
        return (existing ?? throw new InvalidOperationException("fork_link conflict row vanished."), false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ForkLink>> ListBySourceAsync(Guid sourceGcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_list);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sourceGcId });
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var links = new List<ForkLink>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            links.Add(Read(r));
        return links;
    }

    private static ForkLink Read(NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5));
}
