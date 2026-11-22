using Npgsql;

namespace Bazaar.Server.Agents.Data;

/// <summary>
/// Postgres implementation of <see cref="IAgentFirstRunSeeds"/> over the <c>agent</c> table (migration <c>agents/0001</c>).
/// SQL-first, like <see cref="AgentRepository"/>. Only <see cref="RemoveUnstartedAsync"/> runs in a transaction.
/// </summary>
public sealed class PgAgentFirstRunSeeds(NpgsqlDataSource dataSource) : IAgentFirstRunSeeds
{
    private const string UniqueViolation = "23505";

    /// <inheritdoc />
    public async Task<Guid> CreateHeldAsync(string name, string marker, Guid? soulVersionId = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(marker);

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO agent (name, soul_version_id, state, first_run_hold, first_run_marker)
            VALUES (@name, @soul_version_id, 'created', true, @marker)
            RETURNING agent_id
            """);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("soul_version_id", AgentSql.Nullable(soulVersionId));
        cmd.Parameters.AddWithValue("marker", marker);
        try
        {
            return (Guid)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation && ex.ConstraintName == "agent_first_run_marker_key")
        {
            throw new InvalidOperationException("An agent with this first-run marker already exists. Use FindByMarkerAsync.", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ReleaseHoldAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE agent SET first_run_hold = false, updated_at = now()
            WHERE agent_id = @agent_id AND first_run_hold
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryStartAutomaticOnboardingAsync(Guid agentId, CancellationToken ct = default)
    {
        // Created to Onboarding is allowed by AgentLifecycle.CanMove; the hold check is the only extra condition.
        await using var cmd = dataSource.CreateCommand("""
            UPDATE agent SET state = 'onboarding', updated_at = now()
            WHERE agent_id = @agent_id AND state = 'created' AND NOT first_run_hold
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <inheritdoc />
    public async Task<Guid?> FindByMarkerAsync(string marker, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT agent_id FROM agent WHERE first_run_marker = @marker");
        cmd.Parameters.AddWithValue("marker", marker);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is Guid id ? id : null;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveUnstartedAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Locking the agent row first makes a turn insert (which takes a key lock on this row through its foreign key) wait,
        // so a turn cannot start between the check and the delete.
        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT 1 FROM agent WHERE agent_id = @agent_id FOR UPDATE";
            lockCmd.Parameters.AddWithValue("agent_id", agentId);
            if (await lockCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                return false;
        }

        await using (var turnCmd = connection.CreateCommand())
        {
            turnCmd.Transaction = tx;
            turnCmd.CommandText = "SELECT EXISTS (SELECT 1 FROM turn WHERE agent_id = @agent_id)";
            turnCmd.Parameters.AddWithValue("agent_id", agentId);
            if ((bool)(await turnCmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
                return false;
        }

        await using (var inboxCmd = connection.CreateCommand())
        {
            inboxCmd.Transaction = tx;
            inboxCmd.CommandText = "DELETE FROM inbox_item WHERE agent_id = @agent_id AND state = 'pending'";
            inboxCmd.Parameters.AddWithValue("agent_id", agentId);
            await inboxCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var agentCmd = connection.CreateCommand())
        {
            agentCmd.Transaction = tx;
            agentCmd.CommandText = "DELETE FROM agent WHERE agent_id = @agent_id";
            agentCmd.Parameters.AddWithValue("agent_id", agentId);
            if (await agentCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                return false;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
