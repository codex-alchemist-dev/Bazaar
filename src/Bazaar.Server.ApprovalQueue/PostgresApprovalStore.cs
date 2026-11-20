using Npgsql;

namespace Bazaar.Server.ApprovalQueue;

/// <summary>
/// <see cref="IApprovalStore"/> over the shared <see cref="NpgsqlDataSource"/>, SQL-first. The table comes from
/// <c>approval_queue/0001_approval_queue_item.sql</c>. Decide and consume are single guarded UPDATE statements.
/// </summary>
public sealed class PostgresApprovalStore(NpgsqlDataSource dataSource) : IApprovalStore
{
    private const string _columns =
        "id, kind, tier, subject, requester_id, payload_ref, status, requested_at, expires_at, decided_by, decided_at, reason";

    private const string _insertSql = """
        INSERT INTO approval_queue_item (id, kind, tier, subject, requester_id, payload_ref, status, requested_at, expires_at)
        VALUES ($1, $2, $3, $4, $5, $6, 'pending', $7, $8)
        """;

    private const string _getSql = "SELECT " + _columns + " FROM approval_queue_item WHERE id = $1";

    private const string _listSql = "SELECT " + _columns + """

        FROM approval_queue_item
        WHERE status = 'pending' AND expires_at > $1 AND ($2::text IS NULL OR kind = $2)
        ORDER BY requested_at, id
        """;

    private const string _decideSql = """
        UPDATE approval_queue_item
        SET status = $2, decided_by = $3, decided_at = $4, reason = $5
        WHERE id = $1 AND status = 'pending' AND expires_at > $4
        """;

    private const string _consumeSql = """
        UPDATE approval_queue_item SET status = 'consumed', consumed_at = $2
        WHERE id = $1 AND status = 'approved'
        RETURNING id
        """;

    public async Task InsertAsync(ApprovalItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var cmd = dataSource.CreateCommand(_insertSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = item.Id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.Kind });
        cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)item.Tier });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = item.Subject });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = item.RequesterPrincipalId });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = item.PayloadRef });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = item.RequestedAt });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = item.ExpiresAt });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<ApprovalItem?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_getSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ApprovalItem>> ListPendingAsync(string? kind, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_listSql);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = kind });
        var rows = new List<ApprovalItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(Map(reader));
        return rows;
    }

    public async Task<bool> TryDecideAsync(Guid id, Guid approverId, bool approve, string? reason, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_decideSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = approve ? "approved" : "denied" });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = approverId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        cmd.Parameters.Add(new NpgsqlParameter<string?> { TypedValue = reason });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryConsumeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_consumeSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static ApprovalItem Map(NpgsqlDataReader r) => new(
        Id: r.GetGuid(0),
        Kind: r.GetString(1),
        Tier: r.GetInt16(2),
        Subject: r.GetString(3),
        RequesterPrincipalId: r.GetGuid(4),
        PayloadRef: r.IsDBNull(5) ? null : r.GetString(5),
        Status: r.GetString(6) switch
        {
            "pending" => ApprovalStatus.Pending,
            "approved" => ApprovalStatus.Approved,
            "denied" => ApprovalStatus.Denied,
            "consumed" => ApprovalStatus.Consumed,
            var other => throw new InvalidOperationException($"Unknown approval status '{other}'."),
        },
        RequestedAt: r.GetFieldValue<DateTimeOffset>(7),
        ExpiresAt: r.GetFieldValue<DateTimeOffset>(8),
        DecidedBy: r.IsDBNull(9) ? null : r.GetGuid(9),
        DecidedAt: r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        Reason: r.IsDBNull(11) ? null : r.GetString(11));
}
