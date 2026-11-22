using Npgsql;

namespace Bazaar.Server.Analytics.Postgres;

/// <summary>
/// Reads Kanban work events from the append-only <c>task_events</c> log (doc 12) joined to <c>tasks</c>. It couples to the schema,
/// not to the Kanban module's code, and only ever runs SELECT.
/// <list type="bullet">
/// <item>TaskCompleted: a transition to <c>done</c>, credited to the task's owner agent.</item>
/// <item>PrMerged: the same transition when the task has a <c>pr</c> link (Done means the PR merged, DECISIONS-LOG round 8, C28). Left out when <c>includeMerges</c> is false, which is when the forge collector counts merges instead.</item>
/// <item>ReviewPassed: a <c>task.review_approved</c> event, credited to the task's owner agent.</item>
/// <item>ReviewChangesRequested: a transition from <c>review</c> back to <c>in_progress</c>.</item>
/// </list>
/// </summary>
public sealed class KanbanWorkEventCollector(NpgsqlDataSource dataSource, bool includeMerges = true) : IWorkEventCollector
{
    private const string _sql = """
        SELECT t.owner_agent_id::text,
               CASE
                 WHEN e.kind = 'task.review_approved' THEN 1
                 WHEN e.payload->>'to' = 'done' THEN 0
                 ELSE 2
               END AS kind,
               EXISTS (SELECT 1 FROM task_links l WHERE l.task_id = t.id AND l.kind = 'pr') AS has_pr,
               e.at
        FROM task_events e
        JOIN tasks t ON t.id = e.task_id
        WHERE t.owner_agent_id IS NOT NULL
          AND e.at >= @from AND e.at < @to
          AND (e.kind = 'task.review_approved'
               OR (e.kind = 'task.transitioned'
                   AND (e.payload->>'to' = 'done' OR (e.payload->>'from' = 'review' AND e.payload->>'to' = 'in_progress'))))
        """;

    public async Task<IReadOnlyList<AgentWorkEvent>> CollectAsync(AnalyticsWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);

        var events = new List<AgentWorkEvent>();
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.AddWithValue("from", window.From);
        cmd.Parameters.AddWithValue("to", window.To);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var agent = reader.GetString(0);
            var at = reader.GetFieldValue<DateTimeOffset>(3);
            switch (reader.GetInt32(1))
            {
                case 0:
                    events.Add(new AgentWorkEvent(agent, AgentWorkEventKind.TaskCompleted, at));
                    if (includeMerges && reader.GetBoolean(2))
                    {
                        events.Add(new AgentWorkEvent(agent, AgentWorkEventKind.PrMerged, at));
                    }

                    break;
                case 1:
                    events.Add(new AgentWorkEvent(agent, AgentWorkEventKind.ReviewPassed, at));
                    break;
                default:
                    events.Add(new AgentWorkEvent(agent, AgentWorkEventKind.ReviewChangesRequested, at));
                    break;
            }
        }

        return events;
    }
}

/// <summary>
/// Reads reverts from the reputation outcome ledger (doc 11): an <c>outcome_event</c> of kind <c>revert</c>, credited to the agents
/// attributed in <see cref="AuthorRole"/>. This is a SELECT on the ledger's tables only; it references no reputation code and
/// writes nothing back (doc 25, Boundaries).
/// </summary>
public sealed class RevertWorkEventCollector(NpgsqlDataSource dataSource, string authorRole = "author") : IWorkEventCollector
{
    private const string _sql = """
        SELECT DISTINCT a.agent_id, o.id, o.occurred_at
        FROM outcome_event o
        JOIN outcome_attribution a ON a.outcome_id = o.id
        WHERE o.kind = 'revert' AND a.role = @role
          AND o.occurred_at >= @from AND o.occurred_at < @to
        """;

    /// <summary>The attribution role that names the author of the reverted change. Doc 11 does not fix the value, so it is a setting.</summary>
    public string AuthorRole { get; } = authorRole;

    public async Task<IReadOnlyList<AgentWorkEvent>> CollectAsync(AnalyticsWindow window, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(window);

        var events = new List<AgentWorkEvent>();
        await using var cmd = dataSource.CreateCommand(_sql);
        cmd.Parameters.AddWithValue("role", AuthorRole);
        cmd.Parameters.AddWithValue("from", window.From);
        cmd.Parameters.AddWithValue("to", window.To);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            events.Add(new AgentWorkEvent(reader.GetString(0), AgentWorkEventKind.PrReverted, reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return events;
    }
}
