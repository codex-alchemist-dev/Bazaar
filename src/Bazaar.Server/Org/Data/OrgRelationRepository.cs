using Npgsql;

namespace Bazaar.Server.Org.Data;

/// <summary>
/// SQL-first access to <c>org_relation</c> (doc 24 section 2). Cycle, uniqueness and read-only rules live in the database
/// (migration 0018); a violation surfaces as a <see cref="PostgresException"/> with SQLSTATE 23505 (duplicate) or 23514
/// (CHECK or cycle). Routing and loan lifecycle are not here (WP4).
/// </summary>
public sealed class OrgRelationRepository(NpgsqlDataSource dataSource)
{
    private const string _columns = """
        id, kind, from_agent_id, to_agent_id, project_id, starts_at, ends_at, created_by, version
        """;

    private const string _insertSql = """
        INSERT INTO org_relation (id, kind, from_agent_id, to_agent_id, project_id, starts_at, created_by)
        VALUES (@id, @kind, @from_agent_id, @to_agent_id, @project_id, @starts_at, @created_by)
        RETURNING
        """;

    // Ending is the only update. The WHERE clause makes it an optimistic write: a stale version or an ended row matches nothing.
    private const string _endSql = """
        UPDATE org_relation SET ends_at = @ends_at, version = version + 1
        WHERE id = @id AND version = @expected_version AND ends_at IS NULL
        RETURNING
        """;

    /// <summary>Reads one relation without locking it.</summary>
    public async Task<OrgRelation?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM org_relation WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the relations of one scope (<paramref name="projectId"/> null means company-wide), oldest first. Ended rows
    /// are left out unless <paramref name="includeEnded"/> is set.
    /// </summary>
    public async Task<IReadOnlyList<OrgRelation>> ListByScopeAsync(Guid? projectId, bool includeEnded = false, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {_columns} FROM org_relation
            WHERE project_id IS NOT DISTINCT FROM @project_id AND (@include_ended OR ends_at IS NULL)
            ORDER BY starts_at, id
            """);
        cmd.Parameters.AddWithValue("project_id", DbNullable(projectId));
        cmd.Parameters.AddWithValue("include_ended", includeEnded);
        return await ReadManyAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Lists the relations an agent is the subject of, optionally of one kind. Ended rows are left out unless asked for.</summary>
    public async Task<IReadOnlyList<OrgRelation>> ListFromAgentAsync(Guid fromAgentId, OrgRelationKind? kind = null, bool includeEnded = false, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {_columns} FROM org_relation
            WHERE from_agent_id = @from_agent_id AND (@kind::text IS NULL OR kind = @kind) AND (@include_ended OR ends_at IS NULL)
            ORDER BY starts_at, id
            """);
        cmd.Parameters.AddWithValue("from_agent_id", fromAgentId);
        cmd.Parameters.AddWithValue("kind", DbNullable(kind is null ? null : ToSql(kind.Value)));
        cmd.Parameters.AddWithValue("include_ended", includeEnded);
        return await ReadManyAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Inserts a live relation and returns the stored row. Pass <paramref name="tx"/> to write inside a larger unit.</summary>
    public async Task<OrgRelation> InsertAsync(NewOrgRelation relation, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        await using var cmd = Create(tx, $"{_insertSql} {_columns}");
        cmd.Parameters.AddWithValue("id", relation.Id);
        cmd.Parameters.AddWithValue("kind", ToSql(relation.Kind));
        cmd.Parameters.AddWithValue("from_agent_id", relation.FromAgentId);
        cmd.Parameters.AddWithValue("to_agent_id", relation.ToAgentId);
        cmd.Parameters.AddWithValue("project_id", DbNullable(relation.ProjectId));
        cmd.Parameters.AddWithValue("starts_at", relation.StartsAt);
        cmd.Parameters.AddWithValue("created_by", relation.CreatedBy);
        return (await ReadOneAsync(cmd, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Ends a live relation at <paramref name="endsAt"/>, if <paramref name="expectedVersion"/> is current. Returns the
    /// ended row, or null when the version is stale or the row is missing or already ended.
    /// </summary>
    public async Task<OrgRelation?> EndAsync(Guid id, long expectedVersion, DateTimeOffset endsAt, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = Create(tx, $"{_endSql} {_columns}");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("expected_version", expectedVersion);
        cmd.Parameters.AddWithValue("ends_at", endsAt);
        return await ReadOneAsync(cmd, ct).ConfigureAwait(false);
    }

    internal static string ToSql(OrgRelationKind kind) => kind switch
    {
        OrgRelationKind.ReportsTo => "reports_to",
        OrgRelationKind.LentTo => "lent_to",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a stored org relation kind."),
    };

    internal static OrgRelationKind ParseKind(string text) => text switch
    {
        "reports_to" => OrgRelationKind.ReportsTo,
        "lent_to" => OrgRelationKind.LentTo,
        _ => throw new InvalidOperationException($"Unknown org relation kind '{text}'."),
    };

    private NpgsqlCommand Create(NpgsqlTransaction? tx, string sql) =>
        tx is null ? dataSource.CreateCommand(sql) : new NpgsqlCommand(sql, tx.Connection, tx);

    private static object DbNullable<T>(T? value) where T : struct =>
        value.HasValue ? value.Value : DBNull.Value;

    private static object DbNullable(string? value) => (object?)value ?? DBNull.Value;

    private static async Task<OrgRelation?> ReadOneAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async Task<IReadOnlyList<OrgRelation>> ReadManyAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var result = new List<OrgRelation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    private static OrgRelation Read(NpgsqlDataReader reader) => new(
        Id: reader.GetGuid(0),
        Kind: ParseKind(reader.GetString(1)),
        FromAgentId: reader.GetGuid(2),
        ToAgentId: reader.GetGuid(3),
        ProjectId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
        StartsAt: reader.GetFieldValue<DateTimeOffset>(5),
        EndsAt: reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
        CreatedBy: reader.GetGuid(7),
        Version: reader.GetInt64(8));
}
