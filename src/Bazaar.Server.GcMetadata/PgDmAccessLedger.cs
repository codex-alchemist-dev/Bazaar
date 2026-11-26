using Npgsql;

namespace Bazaar.Server.GcMetadata;

/// <summary>Postgres <see cref="IDmAccessLedger"/> over <c>gc_access_request</c> (migration common/0008).</summary>
public sealed class PgDmAccessLedger(NpgsqlDataSource dataSource) : IDmAccessLedger
{
    private const string _columns = "id, gc_id, requester, status, decided_by, created_at, decided_seq";

    /// <inheritdoc />
    public async Task<IReadOnlyList<DmAccessRequest>> ListAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM gc_access_request WHERE gc_id = $1 AND status <> 'withdrawn' ORDER BY created_at, id");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DmAccessRequest?> GetAsync(Guid requestId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM gc_access_request WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requestId });
        return (await ReadAsync(cmd, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<DmAccessRequest> CreateAsync(Guid gcId, Guid requester, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"INSERT INTO gc_access_request (gc_id, requester, created_at) VALUES ($1, $2, $3) RETURNING {_columns}");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requester });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
        return (await ReadAsync(cmd, ct).ConfigureAwait(false)).Single();
    }

    /// <inheritdoc />
    public async Task<bool> ResolveAsync(Guid requestId, DmAccessStatus decision, Guid decidedBy, DateTimeOffset at, CancellationToken ct)
    {
        if (decision == DmAccessStatus.Pending)
            throw new ArgumentOutOfRangeException(nameof(decision), "A decision is approved or denied.");

        // The status = 'pending' guard makes the transition happen once; a second resolve matches no row.
        await using var cmd = dataSource.CreateCommand("UPDATE gc_access_request SET status = $2, decided_by = $3, decided_at = $4 WHERE id = $1 AND status = 'pending' RETURNING id");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requestId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = decision == DmAccessStatus.Approved ? "approved" : "denied" });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = decidedBy });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <inheritdoc />
    public async Task SetDecidedSeqAsync(Guid requestId, long seq, CancellationToken ct)
    {
        // Set once: a resolved request keeps the first seq it was given.
        await using var cmd = dataSource.CreateCommand("UPDATE gc_access_request SET decided_seq = $2 WHERE id = $1 AND status <> 'pending' AND decided_seq IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requestId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = seq });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<DmAccessRequest>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<DmAccessRequest>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new DmAccessRequest(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3) switch
                {
                    "approved" => DmAccessStatus.Approved,
                    "denied" => DmAccessStatus.Denied,
                    _ => DmAccessStatus.Pending,
                },
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }

        return rows;
    }
}
