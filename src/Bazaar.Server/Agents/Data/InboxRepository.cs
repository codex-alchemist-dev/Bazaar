using Npgsql;

namespace Bazaar.Server.Agents.Data;

/// <summary>SQL-first access to <c>inbox_item</c> (doc 06, "Wake model"). Priority is supplied by the caller.</summary>
public sealed class InboxRepository(NpgsqlDataSource dataSource)
{
    /// <summary>
    /// Adds a pending item. Returns false when an item with the same dedupe key already exists (a no-op, doc 06).
    /// </summary>
    public async Task<bool> EnqueueAsync(InboxEnqueue item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(item.Kind);
        if (item.GcId.HasValue != item.GcSeq.HasValue)
            throw new ArgumentException("A GC-backed inbox item needs both GcId and GcSeq.", nameof(item));

        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO inbox_item (agent_id, kind, gc_id, gc_seq, source_ref, priority, due_at)
            VALUES (@agent_id, @kind, @gc_id, @gc_seq, @source_ref, @priority, COALESCE(@due_at, now()))
            ON CONFLICT DO NOTHING
            RETURNING inbox_item_id
            """);
        cmd.Parameters.AddWithValue("agent_id", item.AgentId);
        cmd.Parameters.AddWithValue("kind", item.Kind);
        cmd.Parameters.AddWithValue("gc_id", AgentSql.Nullable(item.GcId));
        cmd.Parameters.AddWithValue("gc_seq", AgentSql.Nullable(item.GcSeq));
        cmd.Parameters.AddWithValue("source_ref", AgentSql.NullableRef(item.SourceRef));
        cmd.Parameters.AddWithValue("priority", item.Priority);
        cmd.Parameters.AddWithValue("due_at", AgentSql.Nullable(item.DueAt));
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
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
