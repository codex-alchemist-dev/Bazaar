using Npgsql;

namespace Bazaar.Server.Skimmer.Data;

/// <summary>SQL-first access to <c>wake_decisions</c> (doc 13, "Idempotency"). Decisions are written once; a repeat is a no-op.</summary>
public sealed class WakeDecisionRepository(NpgsqlDataSource dataSource)
{
    private const string _insertSql = """
        INSERT INTO wake_decisions (id, gc_id, seq, agent_id, path, decision, shadow, score_bp, category, model_id, prompt_version)
        VALUES (@id, @gc_id, @seq, @agent_id, @path, @decision, @shadow, @score_bp, @category, @model_id, @prompt_version)
        ON CONFLICT (gc_id, seq, agent_id, prompt_version) DO NOTHING
        """;

    private const string _columns =
        "id, gc_id, seq, agent_id, path, decision, shadow, score_bp, category, model_id, prompt_version, created_at";

    /// <summary>Records one decision. Returns false when a decision with the same (gc, seq, agent, prompt version) already exists.</summary>
    public async Task<bool> InsertAsync(NewWakeDecision decision, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await using var cmd = SkimmerCommands.Create(dataSource, tx, _insertSql);
        cmd.Parameters.AddWithValue("id", decision.Id);
        cmd.Parameters.AddWithValue("gc_id", decision.GcId);
        cmd.Parameters.AddWithValue("seq", decision.Seq);
        cmd.Parameters.AddWithValue("agent_id", decision.AgentId);
        cmd.Parameters.AddWithValue("path", decision.Path);
        cmd.Parameters.AddWithValue("decision", decision.Decision);
        cmd.Parameters.AddWithValue("shadow", decision.Shadow);
        cmd.Parameters.AddWithValue("score_bp", (object?)decision.ScoreBp ?? DBNull.Value);
        cmd.Parameters.AddWithValue("category", (object?)decision.Category ?? DBNull.Value);
        cmd.Parameters.AddWithValue("model_id", (object?)decision.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("prompt_version", decision.PromptVersion);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>One decision by id, or null.</summary>
    public async Task<WakeDecisionRecord?> GetAsync(Guid id, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = SkimmerCommands.Create(dataSource, tx, $"SELECT {_columns} FROM wake_decisions WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadAsync(cmd, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    /// <summary>The decision for one (gc, seq, agent, prompt version) key, or null.</summary>
    public async Task<WakeDecisionRecord?> FindAsync(Guid gcId, long seq, Guid agentId, string promptVersion, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand($"""
            SELECT {_columns} FROM wake_decisions
            WHERE gc_id = @gc_id AND seq = @seq AND agent_id = @agent_id AND prompt_version = @prompt_version
            """);
        cmd.Parameters.AddWithValue("gc_id", gcId);
        cmd.Parameters.AddWithValue("seq", seq);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("prompt_version", promptVersion);
        return (await ReadAsync(cmd, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    private static async Task<List<WakeDecisionRecord>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var result = new List<WakeDecisionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new WakeDecisionRecord(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return result;
    }
}
