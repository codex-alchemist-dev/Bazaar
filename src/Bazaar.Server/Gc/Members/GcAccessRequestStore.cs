using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Model;
using Npgsql;

namespace Bazaar.Server.Gc.Members;

/// <summary>A row of <c>gc_access_request</c>: a non-owner principal's request to read a DM or Thinking GC.</summary>
/// <param name="Id">Request id.</param>
/// <param name="GcId">The GC the request is for.</param>
/// <param name="Requester">The principal asking to read the GC.</param>
/// <param name="Status">Always <c>pending</c> for rows returned by <see cref="IGcAccessRequestStore.ListPendingAsync"/>.</param>
/// <param name="CreatedAt">When the request was made.</param>
public sealed record GcAccessRequestRecord(Guid Id, Guid GcId, Guid Requester, string Status, DateTimeOffset CreatedAt);

/// <summary>The decisions a pending access request can take. Withdrawal is not a decision made here.</summary>
public enum GcAccessDecision
{
    /// <summary>The requester may read the GC (see <see cref="GcAccessPolicy"/>).</summary>
    Approved,

    /// <summary>The request is refused.</summary>
    Denied,
}

/// <summary>
/// Records DM and Thinking GC access requests and their decisions (docs/design/01-gc-data-model.md, Membership and
/// permissions). Recording a decision never grants membership: the read rule in <see cref="GcAccessPolicy"/> reads
/// the approved rows, and membership is a separate write.
/// </summary>
public interface IGcAccessRequestStore
{
    /// <summary>Records a pending request by <paramref name="requester"/> to read <paramref name="gcId"/>.</summary>
    /// <exception cref="GcNotFoundException">The GC does not exist.</exception>
    Task<Guid> CreateAsync(Guid gcId, Guid requester, CancellationToken ct);

    /// <summary>The pending requests for one GC, oldest first. Resolved requests are not returned.</summary>
    Task<IReadOnlyList<GcAccessRequestRecord>> ListPendingAsync(Guid gcId, CancellationToken ct);

    /// <summary>
    /// Records <paramref name="decision"/> on a pending request, with the resolving principal and time.
    /// Returns false when the request is unknown or already resolved; a resolved request is never changed.
    /// </summary>
    Task<bool> ResolveAsync(Guid requestId, GcAccessDecision decision, Guid decidedBy, DateTimeOffset decidedAt, CancellationToken ct);
}

/// <summary>Postgres-backed <see cref="IGcAccessRequestStore"/> over <c>gc_access_request</c> (migration 0008).</summary>
public sealed class GcAccessRequestStore(NpgsqlDataSource dataSource) : IGcAccessRequestStore
{
    // The GC existence check sits in the same statement, so an unknown GC inserts nothing and no race is possible.
    private const string _createSql = """
        INSERT INTO gc_access_request (gc_id, requester)
        SELECT gc_id, $2 FROM gc WHERE gc_id = $1
        RETURNING id
        """;

    private const string _listSql = """
        SELECT id, gc_id, requester, status, created_at
        FROM gc_access_request
        WHERE gc_id = $1 AND status = 'pending'
        ORDER BY created_at, id
        """;

    // The status = 'pending' guard makes the transition happen once; a second resolve matches no row.
    private const string _resolveSql = """
        UPDATE gc_access_request
        SET status = $2, decided_by = $3, decided_at = $4
        WHERE id = $1 AND status = 'pending'
        RETURNING id
        """;

    /// <inheritdoc />
    public async Task<Guid> CreateAsync(Guid gcId, Guid requester, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(requester, Guid.Empty);

        await using var cmd = dataSource.CreateCommand(_createSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requester });
        var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return id is Guid created ? created : throw new GcNotFoundException(gcId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GcAccessRequestRecord>> ListPendingAsync(Guid gcId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_listSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var rows = new List<GcAccessRequestRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GcAccessRequestRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> ResolveAsync(Guid requestId, GcAccessDecision decision, Guid decidedBy, DateTimeOffset decidedAt, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(decidedBy, Guid.Empty);
        if (!Enum.IsDefined(decision))
            throw new ArgumentOutOfRangeException(nameof(decision));

        await using var cmd = dataSource.CreateCommand(_resolveSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = requestId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = decision == GcAccessDecision.Approved ? "approved" : "denied" });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = decidedBy });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = decidedAt });
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }
}
