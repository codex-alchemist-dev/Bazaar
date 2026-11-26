using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>
/// <see cref="IKanbanExportSource"/> over <c>tasks</c>, <c>task_edges</c>, <c>task_links</c> and <c>task_events</c> (migration
/// <c>common/0012_kanban.sql</c>, doc 12, doc 44 section 4.1). Reads are owner-level, as the port states; the caller decides who may
/// export. Rows stream in the order the port names: ids and edge ends order as lowercase "D" text does, because PostgreSQL's
/// <c>uuid</c> order is bytewise and lowercase hex text sorts the same way.
/// </summary>
/// <remarks>
/// Two values are derived, not stored. <see cref="KanbanTaskRecord.UnfinishedBlockers"/> is the task's blockers that are not Done,
/// the rule <c>BlockedStatus</c> uses. <see cref="KanbanTransitionRecord.Gate"/> is the task's gate now: a <c>task.transitioned</c>
/// event does not store the gate, and the gate is set when the task is created. Only <c>task.transitioned</c> events are returned.
/// </remarks>
public sealed class PostgresKanbanExportSource(NpgsqlDataSource dataSource) : IKanbanExportSource
{
    // $1 is a uuid[] of project ids, or NULL for every board.
    private const string _tasksSql = """
        SELECT t.id, t.project_id, t.title, t.state, t.owner_agent_id, t.gate, t.origin_gc_id, t.origin_message_id, t.due_at,
               t.review_approved_by, t.review_approved_at, t.version, t.created_at, t.updated_at,
               ARRAY(SELECT e.blocker_id FROM task_edges e JOIN tasks b ON b.id = e.blocker_id
                     WHERE e.blocked_id = t.id AND b.state <> 'done' ORDER BY e.blocker_id) AS unfinished_blockers,
               ARRAY(SELECT l.kind || ':' || l.ref FROM task_links l WHERE l.task_id = t.id ORDER BY l.kind, l.ref) AS links
        FROM tasks t
        WHERE ($1::uuid[] IS NULL OR t.project_id = ANY($1))
        ORDER BY t.id
        """;

    private const string _edgesSql = """
        SELECT e.blocker_id, e.blocked_id, e.created_by, e.created_at
        FROM task_edges e JOIN tasks b ON b.id = e.blocker_id
        WHERE ($1::uuid[] IS NULL OR b.project_id = ANY($1))
        ORDER BY e.blocker_id, e.blocked_id
        """;

    private const string _transitionsSql = """
        SELECT v.seq, v.task_id, v.payload ->> 'from', v.payload ->> 'to', v.actor, v.at, v.payload ->> 'reason', t.gate
        FROM task_events v JOIN tasks t ON t.id = v.task_id
        WHERE v.kind = 'task.transitioned' AND ($1::uuid[] IS NULL OR t.project_id = ANY($1))
        ORDER BY v.seq
        """;

    /// <inheritdoc />
    public async IAsyncEnumerable<KanbanTaskRecord> ReadTasksAsync(KanbanExportScope scope, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var cmd = dataSource.CreateCommand(_tasksSql);
        AddScope(cmd, scope);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new KanbanTaskRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                ParseColumn(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetGuid(4),
                ParseGate(reader.GetString(5)),
                reader.GetGuid(6),
                reader.IsDBNull(7) ? null : reader.GetGuid(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetInt64(11),
                reader.GetFieldValue<DateTimeOffset>(12),
                reader.GetFieldValue<DateTimeOffset>(13),
                reader.GetFieldValue<Guid[]>(14),
                reader.GetFieldValue<string[]>(15).Select(ParseLink).ToList());
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KanbanEdgeRecord> ReadEdgesAsync(KanbanExportScope scope, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var cmd = dataSource.CreateCommand(_edgesSql);
        AddScope(cmd, scope);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new KanbanEdgeRecord(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2).ToString("D"), reader.GetFieldValue<DateTimeOffset>(3));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KanbanTransitionRecord> ReadTransitionsAsync(
        KanbanExportScope scope, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await using var cmd = dataSource.CreateCommand(_transitionsSql);
        AddScope(cmd, scope);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new KanbanTransitionRecord(
                reader.GetInt64(0),
                reader.GetGuid(1),
                ParseColumn(reader.IsDBNull(2) ? throw new InvalidOperationException("A transition event has no 'from' state.") : reader.GetString(2)),
                ParseColumn(reader.IsDBNull(3) ? throw new InvalidOperationException("A transition event has no 'to' state.") : reader.GetString(3)),
                reader.GetString(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ParseGate(reader.GetString(7)));
        }
    }

    private static void AddScope(NpgsqlCommand cmd, KanbanExportScope scope) =>
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]?>
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            TypedValue = scope.ProjectIds?.ToArray(),
        });

    private static KanbanColumn ParseColumn(string text) => text switch
    {
        "backlog" => KanbanColumn.Backlog,
        "ready" => KanbanColumn.Ready,
        "in_progress" => KanbanColumn.InProgress,
        "review" => KanbanColumn.Review,
        "done" => KanbanColumn.Done,
        _ => throw new InvalidOperationException($"Unknown task state '{text}'."),
    };

    private static KanbanGate ParseGate(string text) => text switch
    {
        "none" => KanbanGate.None,
        "council" => KanbanGate.Council,
        "owner" => KanbanGate.Owner,
        _ => throw new InvalidOperationException($"Unknown task gate '{text}'."),
    };

    // "kind:ref": the kind never contains a colon, the ref may.
    private static KanbanLinkRecord ParseLink(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 1)
        {
            throw new InvalidOperationException("A task link has no kind.");
        }

        var kind = text[..colon] switch
        {
            "pr" => KanbanLinkKind.Pr,
            "commit" => KanbanLinkKind.Commit,
            "gc" => KanbanLinkKind.Gc,
            "vault_doc" => KanbanLinkKind.VaultDoc,
            var other => throw new InvalidOperationException($"Unknown task link kind '{other}'."),
        };
        return new KanbanLinkRecord(kind, text[(colon + 1)..]);
    }
}
