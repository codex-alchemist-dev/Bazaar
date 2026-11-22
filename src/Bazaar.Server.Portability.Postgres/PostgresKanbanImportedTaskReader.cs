using Npgsql;

namespace Bazaar.Server.Portability.Postgres;

/// <summary>
/// Read side of the Kanban withdrawal (doc 44 section 8.8, PROPOSED): the ids of tasks an import created, from
/// <c>tasks.import_job_id</c> (migration <c>kanban/0002_task_import_job.sql</c>), and the ones already withdrawn, from
/// <c>import_withdrawal</c>. Ids are lowercase "D" text, in id order. The transition itself belongs to the Kanban service and its
/// doc 12 gates; this class never writes to <c>tasks</c>.
/// </summary>
public sealed class PostgresKanbanImportedTaskReader(NpgsqlDataSource dataSource, PostgresImportWithdrawalStore withdrawals) : IImportedKanbanTaskReader
{
    private const string _imported = "SELECT id::text FROM tasks WHERE import_job_id = $1 ORDER BY id";

    /// <summary>Ids of the tasks this job created.</summary>
    public async Task<IReadOnlyList<string>> ReadImportedTaskIdsAsync(string jobId, CancellationToken ct)
    {
        ImportStoreRules.ValidateJobId(jobId);
        await using var cmd = dataSource.CreateCommand(_imported);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = jobId });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    /// <summary>Ids of this job's tasks already withdrawn.</summary>
    public Task<IReadOnlySet<string>> ReadWithdrawnTaskIdsAsync(string jobId, CancellationToken ct) =>
        withdrawals.ReadWithdrawnKeysAsync(jobId, [ImportWithdrawalTargetKind.KanbanTask], ct);
}
