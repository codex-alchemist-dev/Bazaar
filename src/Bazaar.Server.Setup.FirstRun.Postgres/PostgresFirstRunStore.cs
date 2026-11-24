using System.Text.Json.Nodes;
using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>
/// The seeded rows (doc 45 section 1.15): the first <c>general</c> GC and the default agent. Each seed has a marker row in
/// <c>firstrun_seed</c>, written in the same transaction as the thing it marks, under an advisory lock per kind, so a second or a
/// concurrent call finds the first one's rows and adds none. The agent is created in state <c>created</c> (the port's "provisioned")
/// and becomes <c>active</c> only through <see cref="ActivateAgentAsync"/>. Its model policy is exactly the whitelist given; an empty
/// whitelist writes no policy row, which the gateway reads as deny everything. The GC log is append-only, so a rolled-back GC is
/// archived, never deleted.
/// </summary>
public sealed class PostgresFirstRunStore(NpgsqlDataSource dataSource) : IFirstRunStore
{
    /// <summary>The changed-by label on the agent's policy row.</summary>
    public const string PolicyChangedBy = "setup:firstrun";

    private const string _lockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))";
    private const string _markerSql = "SELECT entity_id FROM firstrun_seed WHERE kind = @kind";
    private const string _markerInsertSql = "INSERT INTO firstrun_seed (kind, entity_id, detail) VALUES (@kind, @id, @detail::jsonb)";
    private const string _gcMarkerDeleteSql = "DELETE FROM firstrun_seed WHERE kind = 'general_gc' AND entity_id = @id";
    private const string _agentMarkerDeleteSql = "DELETE FROM firstrun_seed WHERE kind = 'default_agent' AND entity_id = @id";
    private const string _gcLiveSql = "SELECT EXISTS (SELECT 1 FROM gc WHERE gc_id = @id AND archived_at IS NULL)";
    private const string _agentExistsSql = "SELECT EXISTS (SELECT 1 FROM agent WHERE agent_id = @id)";

    private const string _gcInsertSql = """
        INSERT INTO gc (type_key, title, metadata, created_by) VALUES ('general', @title, '{"seed":"firstrun"}'::jsonb, @owner)
        RETURNING gc_id
        """;

    private const string _memberInsertSql = """
        INSERT INTO gc_member (gc_id, principal_id, role) VALUES (@gc, @principal, @role)
        ON CONFLICT (gc_id, principal_id) DO NOTHING
        """;

    private const string _agentInsertSql = "INSERT INTO agent (name, state, model) VALUES (@name, 'created', @model) RETURNING agent_id";

    private const string _policyInsertSql = """
        INSERT INTO agent_model_policy (agent_id, allowed, default_route, version, changed_by, changed_at)
        VALUES (@agent, @allowed::jsonb, @default, 1, @changed_by, now())
        ON CONFLICT (agent_id) DO NOTHING
        """;

    /// <inheritdoc />
    public async Task<SeededGc> EnsureGeneralGcAsync(string title, Guid ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, SeedKinds.GeneralGc, cancellationToken).ConfigureAwait(false);

        if (await FindMarkerAsync(connection, transaction, SeedKinds.GeneralGc, cancellationToken).ConfigureAwait(false) is { } existing
            && await ScalarBoolAsync(connection, transaction, _gcLiveSql, existing, cancellationToken).ConfigureAwait(false))
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SeededGc(existing, false);
        }

        await using var insert = new NpgsqlCommand(_gcInsertSql, connection, transaction);
        insert.Parameters.AddWithValue("title", title.Trim());
        insert.Parameters.AddWithValue("owner", ownerPrincipalId);
        var gcId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await AddMemberAsync(connection, transaction, gcId, ownerPrincipalId, "owner", cancellationToken).ConfigureAwait(false);
        await ReplaceMarkerAsync(connection, transaction, SeedKinds.GeneralGc, gcId, "{}", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SeededGc(gcId, true);
    }

    /// <inheritdoc />
    public async Task<SeededAgent> EnsureDefaultAgentAsync(string name, string purpose, Guid? gcId, IReadOnlyList<string> whitelist, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(whitelist);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, SeedKinds.DefaultAgent, cancellationToken).ConfigureAwait(false);

        if (await FindMarkerAsync(connection, transaction, SeedKinds.DefaultAgent, cancellationToken).ConfigureAwait(false) is { } existing
            && await ScalarBoolAsync(connection, transaction, _agentExistsSql, existing, cancellationToken).ConfigureAwait(false))
        {
            // Unchanged, except that a GC made after the agent still gets its member.
            if (gcId is { } gc) await AddMemberAsync(connection, transaction, gc, existing, "member", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SeededAgent(existing, false);
        }

        await using var insert = new NpgsqlCommand(_agentInsertSql, connection, transaction);
        insert.Parameters.AddWithValue("name", name.Trim());
        insert.Parameters.AddWithValue("model", (object?)ModelBadge.FromRoutes(whitelist) ?? DBNull.Value);
        var agentId = (Guid)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        if (whitelist.Count > 0)
        {
            await using var policy = new NpgsqlCommand(_policyInsertSql, connection, transaction);
            policy.Parameters.AddWithValue("agent", agentId.ToString());
            policy.Parameters.AddWithValue("allowed", new JsonArray([.. whitelist.Select(entry => (JsonNode)JsonValue.Create(entry)!)]).ToJsonString());
            policy.Parameters.AddWithValue("default", whitelist[0]);
            policy.Parameters.AddWithValue("changed_by", PolicyChangedBy);
            await policy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (gcId is { } seededGc) await AddMemberAsync(connection, transaction, seededGc, agentId, "member", cancellationToken).ConfigureAwait(false);
        var detail = new JsonObject { ["purpose"] = purpose ?? "" }.ToJsonString();
        await ReplaceMarkerAsync(connection, transaction, SeedKinds.DefaultAgent, agentId, detail, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SeededAgent(agentId, true);
    }

    /// <inheritdoc />
    public async Task ActivateAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("UPDATE agent SET state = 'active', updated_at = now() WHERE agent_id = @id AND state = 'created'");
        command.Parameters.AddWithValue("id", agentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, SeedKinds.DefaultAgent, cancellationToken).ConfigureAwait(false);

        // The member row is closed at the GC's head, not deleted: membership history stays (doc 01).
        await ExecAsync(connection, transaction, "UPDATE gc_member SET left_seq = (SELECT head_seq FROM gc WHERE gc.gc_id = gc_member.gc_id) WHERE principal_id = @id AND left_seq IS NULL", agentId, cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, transaction, "DELETE FROM agent_model_policy WHERE agent_id = @idtext", agentId, cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, transaction, "DELETE FROM agent WHERE agent_id = @id", agentId, cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, transaction, _agentMarkerDeleteSql, agentId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ArchiveGcAsync(Guid gcId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, SeedKinds.GeneralGc, cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, transaction, "UPDATE gc SET archived_at = now() WHERE gc_id = @id AND archived_at IS NULL", gcId, cancellationToken).ConfigureAwait(false);
        await ExecAsync(connection, transaction, _gcMarkerDeleteSql, gcId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_lockSql, connection, transaction);
        command.Parameters.AddWithValue("key", $"firstrun:{kind}");
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Guid?> FindMarkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_markerSql, connection, transaction);
        command.Parameters.AddWithValue("kind", kind);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }

    // A marker that points at something gone (a rolled-back run) is replaced, so the seed can be made again.
    private static async Task ReplaceMarkerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string kind, Guid id, string detailJson, CancellationToken cancellationToken)
    {
        await using (var delete = new NpgsqlCommand("DELETE FROM firstrun_seed WHERE kind = @kind", connection, transaction))
        {
            delete.Parameters.AddWithValue("kind", kind);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var insert = new NpgsqlCommand(_markerInsertSql, connection, transaction);
        insert.Parameters.AddWithValue("kind", kind);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("detail", detailJson);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddMemberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid gcId, Guid principalId, string role, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(_memberInsertSql, connection, transaction);
        command.Parameters.AddWithValue("gc", gcId);
        command.Parameters.AddWithValue("principal", principalId);
        command.Parameters.AddWithValue("role", role);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ScalarBoolAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task ExecAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, Guid id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        if (sql.Contains("@idtext", StringComparison.Ordinal)) command.Parameters.AddWithValue("idtext", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static class SeedKinds
{
    public const string GeneralGc = "general_gc";
    public const string DefaultAgent = "default_agent";
}
