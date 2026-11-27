using Bazaar.Server.PsychWard.Abstractions;
using Npgsql;

namespace Bazaar.Server.Reputation;

/// <summary>Options for <see cref="OutcomeWardWatcher"/>.</summary>
public sealed class OutcomeWardWatcherOptions
{
    /// <summary>
    /// How many of the newest outcomes on a tag to hand the ward. It must be at least the ward's
    /// <c>ward.repeated_failure_count</c> (three in docs 11 and 26). Shipped default: 10.
    /// </summary>
    public int WindowSize { get; init; } = 10;
}

/// <summary>
/// Tells the Psych Ward when an agent's newest outcomes on one tag all failed (doc 26, section 1: three consecutive failed
/// outcomes on one tag, doc 11). It reads the append-only ledger (<c>outcome_event</c> joined to <c>outcome_attribution</c>), newest
/// first, and hands the ward the window; the ward applies the count. A failed outcome is one with x = 0 (a CI first-run failure, an
/// owner reject, a revert). Call it after an outcome is recorded. The report is best effort: any exception is swallowed, so
/// recording an outcome never fails because of the ward. Agent ids that are not GUIDs are not reported.
/// </summary>
public sealed class OutcomeWardWatcher(NpgsqlDataSource dataSource, IWardTriggerIntake ward, OutcomeWardWatcherOptions options)
{
    /// <summary>Checks one agent and tag. Returns true when the ward admitted the agent.</summary>
    public async Task<bool> CheckAsync(string agentId, string tag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        if (!Guid.TryParse(agentId, out var id))
        {
            return false;
        }

        try
        {
            var window = await ReadWindowAsync(agentId, tag, cancellationToken).ConfigureAwait(false);
            var outcome = await ward.ReportOutcomesAsync(id, tag, window, cancellationToken).ConfigureAwait(false);
            return outcome is { Accepted: true };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<bool>> ReadWindowAsync(string agentId, string tag, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT o.x
            FROM outcome_attribution a
            JOIN outcome_event o ON o.id = a.outcome_id
            WHERE a.agent_id = @agent AND a.tag = @tag
            ORDER BY o.occurred_at DESC, o.recorded_at DESC
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("agent", agentId);
        cmd.Parameters.AddWithValue("tag", tag);
        cmd.Parameters.AddWithValue("limit", options.WindowSize);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var failed = new List<bool>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            failed.Add(reader.GetDecimal(0) == 0m);
        }

        return failed;
    }
}
