using Bazaar.Server.Agents.Turns;
using Npgsql;

namespace Bazaar.RyuseiFuda.Kanban.Data;

/// <summary>Reads which cards a turn was woken for (doc 12, "Assignment wake"). Shared by the context source and the reply target.</summary>
internal static class AssignmentWake
{
    private const string _prefix = "kanban:assign:";

    /// <summary>The card ids named by the <c>assignment</c> items among <paramref name="inboxItemIds"/>, in item order, without repeats.</summary>
    public static async Task<List<Guid>> ReadTaskIdsAsync(NpgsqlDataSource dataSource, IReadOnlyList<Guid> inboxItemIds, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (inboxItemIds.Count == 0)
        {
            return ids;
        }

        await using var cmd = dataSource.CreateCommand(
            "SELECT source_ref FROM inbox_item WHERE inbox_item_id = ANY(@ids) AND kind = 'assignment' AND source_ref LIKE 'kanban:assign:%' ORDER BY created_at, inbox_item_id");
        cmd.Parameters.AddWithValue("ids", inboxItemIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // kanban:assign:<task id>:<version>; the version is not needed, the card is read as it is now.
            var parts = reader.GetString(0)[_prefix.Length..].Split(':');
            if (Guid.TryParse(parts[0], out var id) && !ids.Contains(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}

/// <summary>
/// The reply to a card-assignment wake goes to the card's origin GC (doc 12, "Assignment wake"; doc 06, "Turn loop" step 5). The
/// wake's inbox item has no GC line of its own, so this names the GC from the card.
/// </summary>
public sealed class AssignmentReplyTarget(NpgsqlDataSource dataSource) : ITurnReplyTarget
{
    /// <inheritdoc />
    public async Task<Guid?> ResolveGcAsync(TurnRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var taskId in await AssignmentWake.ReadTaskIdsAsync(dataSource, request.InboxItemIds, cancellationToken).ConfigureAwait(false))
        {
            await using var cmd = dataSource.CreateCommand("SELECT origin_gc_id FROM tasks WHERE id = @id");
            cmd.Parameters.AddWithValue("id", taskId);
            if (await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid gcId)
            {
                return gcId;
            }
        }

        return null;
    }
}
