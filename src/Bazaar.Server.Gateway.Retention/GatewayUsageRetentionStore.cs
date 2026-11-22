using Bazaar.Server.Retention;
using Npgsql;

namespace Bazaar.Server.Gateway.Retention;

/// <summary>
/// The retention store for <c>gw_usage</c> (doc 05, doc 48 section 9). Usage rows are keyed by agent, not person or GC, so no
/// person or GC hold names them; company and category holds still do, through the sweeper. The tombstone is
/// <c>soft_deleted_at</c> (migration <c>gateway/0001</c>), which the usage reader filters. A row is purged only when it carries a
/// tombstone, so the sweeper cannot remove a live row by passing a wrong id.
/// </summary>
public sealed class GatewayUsageRetentionStore(NpgsqlDataSource dataSource) : IRetentionStore
{
    /// <summary>The retention category id: <c>Bazaar:Retention:gateway_usage</c> and the data inventory entry.</summary>
    public const string CategoryId = "gateway_usage";

    // Oldest first, so a bounded run always makes progress from the tail. gw_usage_ts and gw_usage_soft_deleted serve these.
    private const string _listSoftDeleteSql = "SELECT id, ts FROM gw_usage WHERE soft_deleted_at IS NULL AND ts < $1 ORDER BY ts, id LIMIT $2";
    private const string _listPurgeSql = "SELECT id, ts, soft_deleted_at FROM gw_usage WHERE soft_deleted_at < $1 ORDER BY soft_deleted_at, id LIMIT $2";
    private const string _softDeleteSql = "UPDATE gw_usage SET soft_deleted_at = $2 WHERE id = ANY($1) AND soft_deleted_at IS NULL";
    private const string _purgeSql = "DELETE FROM gw_usage WHERE id = ANY($1) AND soft_deleted_at IS NOT NULL";

    public string Category => CategoryId;

    public async Task<IReadOnlyList<RetentionRecordRef>> ListSoftDeleteCandidatesAsync(DateTimeOffset createdBefore, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = dataSource.CreateCommand(_listSoftDeleteSql);
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = createdBefore.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        var rows = new List<RetentionRecordRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new RetentionRecordRef(reader.GetGuid(0).ToString("D"), reader.GetFieldValue<DateTimeOffset>(1), null));
        return rows;
    }

    public async Task<IReadOnlyList<RetentionRecordRef>> ListPurgeCandidatesAsync(DateTimeOffset softDeletedBefore, int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var cmd = dataSource.CreateCommand(_listPurgeSql);
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = softDeletedBefore.UtcDateTime });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = limit });
        var rows = new List<RetentionRecordRef>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new RetentionRecordRef(reader.GetGuid(0).ToString("D"), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<DateTimeOffset>(2)));
        return rows;
    }

    public async Task SoftDeleteAsync(IReadOnlyList<string> ids, DateTimeOffset at, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        await using var cmd = dataSource.CreateCommand(_softDeleteSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = ParseIds(ids) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = at.UtcDateTime });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task PurgeAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        await using var cmd = dataSource.CreateCommand(_purgeSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = ParseIds(ids) });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Guid[] ParseIds(IReadOnlyList<string> ids)
    {
        var parsed = new Guid[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            if (!Guid.TryParse(ids[i], out parsed[i]))
                throw new ArgumentException($"'{ids[i]}' is not a gateway usage id.", nameof(ids));
        }

        return parsed;
    }
}
