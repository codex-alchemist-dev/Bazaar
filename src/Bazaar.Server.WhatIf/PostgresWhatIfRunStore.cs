using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.WhatIf;

/// <summary>
/// SQL-first store over <c>what_if_run</c> (migration components/what_if/0001, doc 26 Data). One row per branch; a save replaces all
/// rows of the run in one transaction.
/// </summary>
public sealed class PostgresWhatIfRunStore(NpgsqlDataSource dataSource) : IWhatIfRunStore
{
    private const string _delete = "DELETE FROM what_if_run WHERE id = @id";

    private const string _insert = """
        INSERT INTO what_if_run (id, branch, decision_ref, source_gc_id, fork_seq, metrics_spec_hash, status, tasks, result, abort_reason, position, created_at)
        VALUES (@id, @branch, @decision_ref, @source_gc_id, @fork_seq, @metrics_spec_hash, @status, @tasks, @result, @abort_reason, @position, @created_at)
        """;

    private const string _select = """
        SELECT branch, decision_ref, source_gc_id, fork_seq, metrics_spec_hash, status, tasks::text, result::text, abort_reason, created_at
        FROM what_if_run WHERE id = @id ORDER BY position
        """;

    public async Task SaveAsync(WhatIfRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var del = new NpgsqlCommand(_delete, conn, tx))
        {
            del.Parameters.AddWithValue("id", run.Id);
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        for (var i = 0; i < run.Branches.Count; i++)
        {
            var b = run.Branches[i];
            await using var cmd = new NpgsqlCommand(_insert, conn, tx);
            cmd.Parameters.AddWithValue("id", run.Id);
            cmd.Parameters.AddWithValue("branch", b.Name);
            cmd.Parameters.AddWithValue("decision_ref", run.DecisionRef);
            cmd.Parameters.AddWithValue("source_gc_id", run.SourceGcId);
            cmd.Parameters.AddWithValue("fork_seq", run.ForkSeq);
            cmd.Parameters.AddWithValue("metrics_spec_hash", run.MetricsSpecHash);
            cmd.Parameters.AddWithValue("status", b.State.ToString());
            cmd.Parameters.Add(new NpgsqlParameter("tasks", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(b.Tasks.ToArray(), WhatIfJson.Default.StringArray) });
            cmd.Parameters.Add(new NpgsqlParameter("result", NpgsqlDbType.Jsonb)
            {
                Value = b.Result is null ? DBNull.Value : JsonSerializer.Serialize(b.Result, WhatIfJson.Default.WhatIfBranchResult),
            });
            cmd.Parameters.Add(new NpgsqlParameter("abort_reason", NpgsqlDbType.Text) { Value = (object?)b.AbortReason ?? DBNull.Value });
            cmd.Parameters.AddWithValue("position", i);
            cmd.Parameters.AddWithValue("created_at", run.CreatedAt);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<WhatIfRun?> GetAsync(Guid runId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_select);
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var branches = new List<WhatIfBranchRun>();
        string? decisionRef = null;
        string? specHash = null;
        Guid sourceGc = default;
        long forkSeq = 0;
        DateTimeOffset created = default;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            decisionRef = reader.GetString(1);
            sourceGc = reader.GetGuid(2);
            forkSeq = reader.GetInt64(3);
            specHash = reader.GetString(4);
            created = reader.GetFieldValue<DateTimeOffset>(9);
            var tasks = JsonSerializer.Deserialize(reader.GetString(6), WhatIfJson.Default.StringArray) ?? [];
            var result = reader.IsDBNull(7) ? null : JsonSerializer.Deserialize(reader.GetString(7), WhatIfJson.Default.WhatIfBranchResult);
            branches.Add(new WhatIfBranchRun(
                reader.GetString(0), tasks, Enum.Parse<WhatIfRunState>(reader.GetString(5)), result,
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return branches.Count == 0 ? null : new WhatIfRun(runId, decisionRef!, sourceGc, forkSeq, specHash!, branches, created);
    }
}
