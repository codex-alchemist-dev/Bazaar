using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Bazaar.Server.ThinkingGc;

/// <summary>
/// Finds or creates the Thinking GC for an agent under a working GC (doc 01, Thinking GCs [DECIDED, round 3]). The GC has type
/// <c>thinking</c>, <c>parent_gc_id</c> = the working GC, and the agent as its only member row; the owner reads every GC by policy
/// and has no row. The id is derived from the working GC and the agent, so there is exactly one per pair and a concurrent create
/// lands on the same row.
/// </summary>
/// <remarks>
/// The refusal rules mirror <c>ThinkingGcRules.Create</c> in Bazaar.Server, which a module cannot reference: the working GC must be
/// live and not a Thinking GC, and the agent must be an active member. Tests keep the two in step.
/// </remarks>
public sealed class ThinkingGcStore(NpgsqlDataSource dataSource)
{
    /// <summary>The GC type key (seeded in the core schema).</summary>
    public const string TypeKey = "thinking";

    /// <summary>The id of the Thinking GC for this agent under this working GC. Always the same for the same pair.</summary>
    public static Guid DeterministicId(Guid workingGcId, Guid agentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"thinking-gc:{workingGcId:D}:{agentId:D}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    /// <summary>
    /// Returns the Thinking GC id, creating the GC and the agent's member row when this is the first call. Null when the rules refuse:
    /// unknown, archived or Thinking working GC, or an agent that is not an active member of it.
    /// </summary>
    public async Task<Guid?> EnsureAsync(Guid workingGcId, Guid agentId, CancellationToken ct)
    {
        var id = DeterministicId(workingGcId, agentId);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Fast path: the common call after the first one.
        if (await ExistsAsync(conn, id, ct).ConfigureAwait(false))
            return id;

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        const string workingSql = """
            SELECT g.type_key, g.archived_at IS NOT NULL,
                   EXISTS (SELECT 1 FROM gc_member_current m
                           WHERE m.gc_id = g.gc_id AND m.principal_id = @agent AND m.left_seq IS NULL AND m.role = 'member')
            FROM gc g WHERE g.gc_id = @gc
            """;
        await using (var cmd = new NpgsqlCommand(workingSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("gc", workingGcId);
            cmd.Parameters.AddWithValue("agent", agentId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;
            if (reader.GetString(0) == TypeKey || reader.GetBoolean(1) || !reader.GetBoolean(2))
                return null;
        }

        const string insertGc = """
            INSERT INTO gc (gc_id, parent_gc_id, type_key, title, created_by, metadata)
            VALUES (@id, @parent, 'thinking', @title, @agent, @metadata)
            ON CONFLICT (gc_id) DO NOTHING
            """;
        bool created;
        await using (var cmd = new NpgsqlCommand(insertGc, conn, tx))
        {
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("parent", workingGcId);
            cmd.Parameters.AddWithValue("title", "Thinking");
            cmd.Parameters.AddWithValue("agent", agentId);
            cmd.Parameters.Add(new NpgsqlParameter("metadata", NpgsqlTypes.NpgsqlDbType.Jsonb)
            {
                Value = $$"""{"agent":"{{agentId:D}}"}""",
            });
            created = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }

        if (created)
        {
            // gc_member is append-only and its key is (gc_id, principal_id), so the row is written only by the call that made the GC.
            const string insertMember = "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES (@id, @agent, 'member', 0)";
            await using var cmd = new NpgsqlCommand(insertMember, conn, tx);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("agent", agentId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return id;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM gc WHERE gc_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }
}
