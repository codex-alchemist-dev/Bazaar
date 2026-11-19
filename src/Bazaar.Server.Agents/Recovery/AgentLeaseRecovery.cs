using Bazaar.Server.Agents.Data;
using Npgsql;

namespace Bazaar.Server.Agents.Recovery;

/// <summary>Counts from one recovery pass. Every count is zero on a pass that finds nothing to do.</summary>
public sealed record LeaseRecoveryReport(int ExpiredLeases, int TurnsAbandoned, int StepsAbandoned, int StepsUnknown, int ItemsReleased);

/// <summary>
/// Crash recovery for expired agent leases (doc 06, "Concurrency and recovery"). For each expired lease, in one
/// transaction: the lease is removed, the agent's running turns are abandoned, unfinished steps are marked
/// (a model call or an idempotent tool call is abandoned so it can be re-issued; any other tool, post or memory step is
/// unknown so the model is told), and the dead holder's claimed inbox items return to pending.
/// Completed steps are never touched. A live or renewed lease is never touched, because only rows still expired at
/// delete time are taken. Running a pass again finds nothing new.
/// </summary>
public sealed class AgentLeaseRecovery(NpgsqlDataSource dataSource)
{
    public async Task<LeaseRecoveryReport> RecoverExpiredAsync(CancellationToken ct = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Taking the expired rows with DELETE locks them. A holder that renews first makes its row unexpired, so it is skipped.
        var expired = new List<(Guid AgentId, string Holder)>();
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM agent_lease WHERE expires_at <= now() RETURNING agent_id, holder", connection, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                expired.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        var turns = 0;
        var abandoned = 0;
        var unknown = 0;
        var released = 0;
        foreach (var (agentId, holder) in expired)
        {
            (var stepAbandoned, var stepUnknown) = await MarkUnfinishedStepsAsync(connection, tx, agentId, ct).ConfigureAwait(false);
            abandoned += stepAbandoned;
            unknown += stepUnknown;
            turns += await AbandonRunningTurnsAsync(connection, tx, agentId, ct).ConfigureAwait(false);
            released += await ReleaseClaimsAsync(connection, tx, holder, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new LeaseRecoveryReport(expired.Count, turns, abandoned, unknown, released);
    }

    /// <summary>Marks the agent's pending steps on running turns. Returns how many were abandoned and how many were unknown.</summary>
    private static async Task<(int Abandoned, int Unknown)> MarkUnfinishedStepsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE turn_step AS s
            SET status = CASE WHEN s.kind = 'model' OR (s.kind = 'tool' AND s.idempotent) THEN 'abandoned' ELSE 'unknown' END
            FROM turn AS t
            WHERE s.turn_id = t.turn_id AND t.agent_id = @agent_id AND t.status = 'running' AND s.status = 'pending'
            RETURNING s.status
            """, connection, tx);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        var abandoned = 0;
        var unknown = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (reader.GetString(0) == "abandoned")
                abandoned++;
            else
                unknown++;
        }

        return (abandoned, unknown);
    }

    private static async Task<int> AbandonRunningTurnsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, Guid agentId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE turn SET status = 'abandoned', ended_at = now()
            WHERE agent_id = @agent_id AND status = 'running'
            """, connection, tx);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> ReleaseClaimsAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string holder, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE inbox_item SET state = 'pending', claimed_by = NULL, claimed_at = NULL
            WHERE state = 'claimed' AND claimed_by = @holder
            """, connection, tx);
        cmd.Parameters.AddWithValue("holder", holder);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
