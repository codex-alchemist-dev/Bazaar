using Npgsql;

namespace Bazaar.Server.Agents.Data;

/// <summary>
/// The per-agent lease (doc 06, "Concurrency and recovery"): one row per agent, so at most one active turn.
/// Expiry is compared with database <c>now()</c>. A lease is taken only when free or expired.
/// </summary>
public sealed class LeaseRepository(NpgsqlDataSource dataSource)
{
    /// <summary>Takes the lease if it is free or expired. Returns false while another holder's lease is live.</summary>
    public async Task<bool> TryAcquireAsync(Guid agentId, string holder, TimeSpan duration, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO agent_lease (agent_id, holder, acquired_at, expires_at)
            VALUES (@agent_id, @holder, now(), now() + make_interval(secs => @seconds))
            ON CONFLICT (agent_id) DO UPDATE
            SET holder = EXCLUDED.holder, acquired_at = now(), expires_at = EXCLUDED.expires_at
            WHERE agent_lease.expires_at <= now()
            RETURNING agent_id
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("holder", holder);
        cmd.Parameters.AddWithValue("seconds", duration.TotalSeconds);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <summary>Extends a live lease held by <paramref name="holder"/>. Returns false if the lease expired or moved.</summary>
    public async Task<bool> RenewAsync(Guid agentId, string holder, TimeSpan duration, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE agent_lease SET expires_at = now() + make_interval(secs => @seconds)
            WHERE agent_id = @agent_id AND holder = @holder AND expires_at > now()
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("holder", holder);
        cmd.Parameters.AddWithValue("seconds", duration.TotalSeconds);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>Releases the lease if <paramref name="holder"/> still holds it.</summary>
    public async Task<bool> ReleaseAsync(Guid agentId, string holder, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM agent_lease WHERE agent_id = @agent_id AND holder = @holder");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("holder", holder);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<LeaseRecord?> GetAsync(Guid agentId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT agent_id, holder, acquired_at, expires_at FROM agent_lease WHERE agent_id = @agent_id");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;
        return new LeaseRecord(
            reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3));
    }
}
