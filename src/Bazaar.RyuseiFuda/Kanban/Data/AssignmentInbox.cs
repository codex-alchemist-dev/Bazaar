using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Inbox;
using Npgsql;

namespace Bazaar.RyuseiFuda.Kanban.Data;

/// <summary>
/// Writes the <c>assignment</c> inbox item that wakes a card's new assignee (doc 12, "Assignment wake"; doc 06, "Wake model").
/// It runs on the card change's own transaction, so the item commits or rolls back with the assignee change.
/// </summary>
public static class AssignmentInbox
{
    /// <summary>The item's dedupe key: one per card and per version the change produced, so a retried change is a no-op.</summary>
    public static string SourceRef(Guid taskId, long versionAfter) => $"kanban:assign:{taskId:D}:{versionAfter}";

    /// <summary>
    /// Enqueues one item for <paramref name="newAssignee"/> when the assignee changed to an agent that is not the actor and can be
    /// woken (<see cref="RyuseiFireWriter.TargetStatus"/>). Returns whether a new item was written.
    /// </summary>
    public static async Task<bool> EnqueueAsync(
        NpgsqlTransaction tx, Guid taskId, Guid? previousAssignee, Guid? newAssignee, TaskActor actor, long versionAfter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tx);
        if (newAssignee is not { } agentId || agentId == previousAssignee)
        {
            return false;
        }

        // Loop guard: the actor who made the change is never woken by it.
        if (actor.Kind == TaskActorKind.Agent && actor.Id == agentId)
        {
            return false;
        }

        await using var cmd = new NpgsqlCommand("SELECT state FROM agent WHERE agent_id = @id", tx.Connection, tx);
        cmd.Parameters.AddWithValue("id", agentId);
        var state = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (RyuseiFireWriter.TargetStatus(state) != RyuseiFireTargetStatus.Wakeable)
        {
            return false;
        }

        var priority = checked((short)InboxPriority.Rank(InboxKind.Assignment));
        return await InboxRepository.EnqueueAsync(
            tx, new InboxEnqueue(agentId, InboxKind.Assignment, priority, SourceRef: SourceRef(taskId, versionAfter)), ct).ConfigureAwait(false);
    }
}
