using Npgsql;

namespace Bazaar.Server.Agents.Data;

/// <summary>SQL-first access to <c>inbox_item</c> (doc 06, "Wake model"). Priority is supplied by the caller.</summary>
public sealed class InboxRepository(NpgsqlDataSource dataSource)
{
    private const string _enqueueSql = """
        INSERT INTO inbox_item (agent_id, kind, gc_id, gc_seq, source_ref, priority, due_at)
        VALUES (@agent_id, @kind, @gc_id, @gc_seq, @source_ref, @priority, COALESCE(@due_at, now()))
        ON CONFLICT DO NOTHING
        RETURNING inbox_item_id
        """;

    /// <summary>
    /// Adds a pending item. Returns false when an item with the same dedupe key already exists (a no-op, doc 06).
    /// </summary>
    public async Task<bool> EnqueueAsync(InboxEnqueue item, CancellationToken ct = default)
    {
        ValidateEnqueue(item);
        await using var cmd = dataSource.CreateCommand(_enqueueSql);
        BindEnqueue(cmd, item);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// Adds a pending item inside <paramref name="tx"/>, so the item commits or rolls back with the caller's other writes
    /// (for example, a RyuseiFuda fire, doc 38). Same dedupe and return value as <see cref="EnqueueAsync(InboxEnqueue, CancellationToken)"/>.
    /// </summary>
    public static async Task<bool> EnqueueAsync(NpgsqlTransaction tx, InboxEnqueue item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ValidateEnqueue(item);
        await using var cmd = new NpgsqlCommand(_enqueueSql, tx.Connection, tx);
        BindEnqueue(cmd, item);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static void ValidateEnqueue(InboxEnqueue item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Kind);
        if (item.GcId.HasValue != item.GcSeq.HasValue)
            throw new ArgumentException("A GC-backed inbox item needs both GcId and GcSeq.", nameof(item));
    }

    private static void BindEnqueue(NpgsqlCommand cmd, InboxEnqueue item)
    {
        cmd.Parameters.AddWithValue("agent_id", item.AgentId);
        cmd.Parameters.AddWithValue("kind", item.Kind);
        cmd.Parameters.AddWithValue("gc_id", AgentSql.Nullable(item.GcId));
        cmd.Parameters.AddWithValue("gc_seq", AgentSql.Nullable(item.GcSeq));
        cmd.Parameters.AddWithValue("source_ref", AgentSql.NullableRef(item.SourceRef));
        cmd.Parameters.AddWithValue("priority", item.Priority);
        cmd.Parameters.AddWithValue("due_at", AgentSql.Nullable(item.DueAt));
    }

    /// <summary>
    /// Reads up to <paramref name="limit"/> due pending items, in the same order <see cref="ClaimAsync"/> claims them
    /// (priority, due time, id). Nothing is claimed or locked, so a caller can read the batch and claim it later by id.
    /// </summary>
    public async Task<IReadOnlyList<InboxRow>> PeekDueAsync(Guid agentId, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var cmd = dataSource.CreateCommand("""
            SELECT inbox_item_id, kind, priority, gc_id, gc_seq, source_ref
            FROM inbox_item
            WHERE agent_id = @agent_id AND state = 'pending' AND due_at <= now()
            ORDER BY priority, due_at, inbox_item_id
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("limit", limit);

        var rows = new List<InboxRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new InboxRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetInt16(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return rows;
    }

    /// <summary>
    /// Reads up to <paramref name="limit"/> agents that have at least one due pending item and whose agent state allows waking
    /// (only <c>active</c>; resting, onboarding, created and retired agents do not wake, doc 06 "Lifecycle"). Ordered by each
    /// agent's best due priority, then its oldest due time. Read only: no lock and no claim.
    /// </summary>
    public async Task<IReadOnlyList<DueAgent>> PeekDueAgentsAsync(int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var cmd = dataSource.CreateCommand("""
            SELECT i.agent_id, min(i.priority) AS best_priority, min(i.due_at) AS oldest_due_at
            FROM inbox_item AS i
            JOIN agent AS a ON a.agent_id = i.agent_id
            WHERE i.state = 'pending' AND i.due_at <= now() AND a.state = 'active'
            GROUP BY i.agent_id
            ORDER BY best_priority, oldest_due_at, i.agent_id
            LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("limit", limit);

        var agents = new List<DueAgent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            agents.Add(new DueAgent(reader.GetGuid(0), reader.GetInt16(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return agents;
    }

    /// <summary>
    /// Claims up to <paramref name="limit"/> due pending items for <paramref name="holder"/>, highest priority first.
    /// Rows locked by another claim are skipped, so two claims never get the same item.
    /// </summary>
    public async Task<IReadOnlyList<InboxRow>> ClaimAsync(Guid agentId, string holder, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        await using var cmd = dataSource.CreateCommand("""
            WITH due AS (
                SELECT inbox_item_id FROM inbox_item
                WHERE agent_id = @agent_id AND state = 'pending' AND due_at <= now()
                ORDER BY priority, due_at, inbox_item_id
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            UPDATE inbox_item AS i
            SET state = 'claimed', claimed_by = @holder, claimed_at = now()
            FROM due
            WHERE i.inbox_item_id = due.inbox_item_id
            RETURNING i.inbox_item_id, i.kind, i.priority, i.gc_id, i.gc_seq, i.source_ref
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("holder", holder);
        cmd.Parameters.AddWithValue("limit", limit);

        var rows = new List<InboxRow>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new InboxRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetInt16(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        // UPDATE ... RETURNING does not promise an order; the caller gets the claim order back.
        rows.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return rows;
    }

    /// <summary>
    /// Claims the named due items that are still pending, for a turn whose caller chose them (doc 06 "Turn loop" step 1).
    /// Items in any other state are skipped. Returns the ids this call claimed, which may be fewer than <paramref name="ids"/>.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ClaimByIdAsync(Guid agentId, string holder, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holder);
        if (ids.Count == 0)
            return [];
        await using var cmd = dataSource.CreateCommand("""
            UPDATE inbox_item
            SET state = 'claimed', claimed_by = @holder, claimed_at = now()
            WHERE agent_id = @agent_id AND state = 'pending' AND due_at <= now() AND inbox_item_id = ANY(@ids)
            RETURNING inbox_item_id
            """);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("holder", holder);
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { ParameterName = "ids", TypedValue = [.. ids] });

        var claimed = new List<Guid>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                claimed.Add(reader.GetGuid(0));
        }

        return claimed;
    }

    /// <summary>Marks items claimed by <paramref name="holder"/> as done. Returns how many changed.</summary>
    public async Task<int> CompleteAsync(string holder, IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return 0;
        await using var cmd = dataSource.CreateCommand("""
            UPDATE inbox_item
            SET state = 'done', done_at = now(), claimed_by = NULL, claimed_at = NULL
            WHERE state = 'claimed' AND claimed_by = @holder AND inbox_item_id = ANY(@ids)
            """);
        cmd.Parameters.AddWithValue("holder", holder);
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { ParameterName = "ids", TypedValue = [.. ids] });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns every item claimed by <paramref name="holder"/> to pending. Used when a turn fails or is recovered.</summary>
    public async Task<int> ReleaseAsync(string holder, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            UPDATE inbox_item
            SET state = 'pending', claimed_by = NULL, claimed_at = NULL
            WHERE state = 'claimed' AND claimed_by = @holder
            """);
        cmd.Parameters.AddWithValue("holder", holder);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(Guid agentId, InboxState state, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM inbox_item WHERE agent_id = @agent_id AND state = @state");
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("state", AgentSql.ToDb(state));
        return (int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }
}
