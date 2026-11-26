using System.Globalization;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Context;
using Npgsql;

namespace Bazaar.RyuseiFuda.Kanban.Data;

/// <summary>
/// Gives a turn that was woken by a card assignment the card it was woken for (doc 12, "Assignment wake"; doc 06, "Context").
/// The wake's inbox item carries only the source key <c>kanban:assign:&lt;task id&gt;:&lt;version&gt;</c>, so without this source the
/// assembler has nothing to show the model and the turn fails with no messages. The card goes into the
/// <see cref="ContextSection.TaskState"/> section and the wake itself into <see cref="ContextSection.Inbox"/>.
/// </summary>
/// <remarks>Reads the card as it is now, not as it was when the item was written, so a card that moved since still shows its current column.</remarks>
public sealed class AssignmentContextSource(NpgsqlDataSource dataSource) : IContextSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ContextSourceItem>> ReadAsync(TurnContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.InboxItemIds.Count == 0)
        {
            return [];
        }

        var items = new List<ContextSourceItem>();
        foreach (var taskId in await AssignmentWake.ReadTaskIdsAsync(dataSource, request.InboxItemIds, cancellationToken).ConfigureAwait(false))
        {
            await using var cmd = dataSource.CreateCommand(
                "SELECT title, state, project_id, origin_gc_id, version FROM tasks WHERE id = @id");
            cmd.Parameters.AddWithValue("id", taskId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var title = reader.GetString(0);
            var state = reader.GetString(1);
            var projectId = reader.GetGuid(2);
            var gcId = reader.GetGuid(3);
            var version = reader.GetInt64(4).ToString(CultureInfo.InvariantCulture);
            var card = string.Create(
                CultureInfo.InvariantCulture,
                $"Card {taskId:D} \"{title}\" (project {projectId:D}, column {state}, discussed in GC {gcId:D}).");
            items.Add(new ContextSourceItem(taskId.ToString("D"), version, card, ContextSection.TaskState));
            items.Add(new ContextSourceItem(
                taskId.ToString("D"),
                version,
                $"You were assigned this card. Do the work, report in the card's GC, and move it with kanban.transition when it is ready for review. {card}",
                ContextSection.Inbox));
        }

        return items;
    }
}
