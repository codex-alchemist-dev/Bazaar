using Npgsql;

namespace Bazaar.Server.Kanban.Data;

/// <summary>SQL-first access to <c>task_gate_decisions</c> (doc 12, WP9). Decisions are append-only; the latest one wins.</summary>
public sealed class TaskGateRepository(NpgsqlDataSource dataSource)
{
    private const string _insertSql = """
        INSERT INTO task_gate_decisions (task_id, gate, outcome, decided_by, reference)
        VALUES (@task_id, @gate, @outcome, @decided_by, @reference)
        """;

    private const string _latestSql = """
        SELECT outcome FROM task_gate_decisions
        WHERE task_id = @task_id AND gate = @gate
        ORDER BY decided_at DESC, decision_id DESC
        LIMIT 1
        """;

    public async Task InsertAsync(NpgsqlTransaction tx, Guid taskId, TaskGate gate, TaskGateOutcome outcome, Guid decidedBy, string reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        await using var cmd = KanbanCommands.Create(dataSource, tx, _insertSql);
        cmd.Parameters.AddWithValue("task_id", taskId);
        cmd.Parameters.AddWithValue("gate", KanbanText.ToSql(gate));
        cmd.Parameters.AddWithValue("outcome", KanbanText.ToSql(outcome));
        cmd.Parameters.AddWithValue("decided_by", decidedBy);
        cmd.Parameters.AddWithValue("reference", reference);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The latest outcome for the task's gate, or null when nothing has been decided.</summary>
    public async Task<TaskGateOutcome?> LatestAsync(Guid taskId, TaskGate gate, NpgsqlTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = KanbanCommands.Create(dataSource, tx, _latestSql);
        cmd.Parameters.AddWithValue("task_id", taskId);
        cmd.Parameters.AddWithValue("gate", KanbanText.ToSql(gate));
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null ? null : KanbanText.ParseOutcome((string)value);
    }
}
