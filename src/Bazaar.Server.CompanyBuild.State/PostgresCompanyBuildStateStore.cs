using Npgsql;

namespace Bazaar.Server.CompanyBuild.State;

/// <summary>
/// <see cref="ICompanyBuildStateStore"/> over the shared <see cref="NpgsqlDataSource"/>, SQL-first. Tables come from
/// <c>company_build_state/0001_company_build_state.sql</c>. A commit is one guarded UPDATE on revision. A build start is one
/// INSERT whose two unique indexes (idempotency key, one running build) decide the outcome.
/// </summary>
public sealed class PostgresCompanyBuildStateStore(NpgsqlDataSource dataSource) : ICompanyBuildStateStore
{
    private const string _selectionColumns =
        "revision, selected_module_ids, pending_approval_modules, pending_approval_ids, updated_by, updated_at";

    private const string _buildColumns =
        "id, selection_revision, status, started_by, started_at, finished_at, idempotency_key, attempts, result_summary";

    private const string _runningIndex = "company_build_one_running_idx";

    private const string _getSelectionSql = "SELECT " + _selectionColumns + " FROM company_module_selection WHERE id = 1";

    private const string _commitSql = """
        UPDATE company_module_selection
        SET revision = revision + 1, selected_module_ids = $2, pending_approval_modules = $3, pending_approval_ids = $4,
            updated_by = $5, updated_at = $6
        WHERE id = 1 AND revision = $1
        RETURNING revision
        """;

    private const string _setPendingSql = """
        UPDATE company_module_selection SET pending_approval_modules = $2, pending_approval_ids = $3
        WHERE id = 1 AND revision = $1
        """;

    private const string _listBuildsSql = "SELECT " + _buildColumns + " FROM company_build ORDER BY started_at DESC, id LIMIT $1";

    private const string _getByKeySql = "SELECT " + _buildColumns + " FROM company_build WHERE idempotency_key = $1";

    private const string _beginSql = """
        INSERT INTO company_build (id, selection_revision, status, started_by, started_at, idempotency_key, attempts)
        VALUES ($1, $2, 'running', $3, $4, $5, 0)
        ON CONFLICT (idempotency_key) DO NOTHING
        RETURNING id
        """;

    private const string _completeSql = """
        UPDATE company_build
        SET status = $2, finished_at = $3, attempts = $4, result_summary = $5
        WHERE id = $1 AND status = 'running'
        """;

    private const string _failRunningSql = """
        UPDATE company_build SET status = 'failed', finished_at = $1, result_summary = $2 WHERE status = 'running'
        """;

    public async Task<SelectionState> GetSelectionAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_getSelectionSql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        // The migration seeds the row, so a missing one means the schema is not migrated.
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("company_module_selection has no row. Run the migrations.");
        return MapSelection(reader, 0);
    }

    public async Task<SelectionState?> TryCommitSelectionAsync(
        long expectedRevision,
        IReadOnlyList<string> selectedModuleIds,
        IReadOnlyDictionary<string, Guid> pendingApprovals,
        Guid actor,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var pending = pendingApprovals.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        await using var cmd = dataSource.CreateCommand(_commitSql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = expectedRevision });
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. selectedModuleIds] });
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. pending.Select(p => p.Key)] });
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. pending.Select(p => p.Value)] });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = actor });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long revision)
            return null;
        return new SelectionState(revision, [.. selectedModuleIds], pendingApprovals, actor, now);
    }

    public async Task<bool> TrySetPendingApprovalsAsync(long revision, IReadOnlyDictionary<string, Guid> pendingApprovals, CancellationToken ct = default)
    {
        var pending = pendingApprovals.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        await using var cmd = dataSource.CreateCommand(_setPendingSql);
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = revision });
        cmd.Parameters.Add(new NpgsqlParameter<string[]> { TypedValue = [.. pending.Select(p => p.Key)] });
        cmd.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = [.. pending.Select(p => p.Value)] });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<BuildRow>> ListBuildsAsync(int limit, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_listBuildsSql);
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(1, limit) });
        var rows = new List<BuildRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(MapBuild(reader));
        return rows;
    }

    public async Task<BuildRow?> GetBuildByKeyAsync(string idempotencyKey, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_getByKeySql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = idempotencyKey });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? MapBuild(reader) : null;
    }

    public async Task<BuildBeginResult> TryBeginBuildAsync(BuildRow running, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(running);
        try
        {
            await using var cmd = dataSource.CreateCommand(_beginSql);
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = running.Id });
            cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = running.SelectionRevision });
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = running.StartedBy });
            cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = running.StartedAt });
            cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = running.IdempotencyKey });
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
                return new BuildBeginResult(BuildBeginOutcome.Started, null);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == _runningIndex)
        {
            return new BuildBeginResult(BuildBeginOutcome.AlreadyRunning, null);
        }

        // ON CONFLICT swallowed the insert: the key already has a build.
        var existing = await GetBuildByKeyAsync(running.IdempotencyKey, ct).ConfigureAwait(false);
        return new BuildBeginResult(BuildBeginOutcome.DuplicateKey, existing);
    }

    public async Task CompleteBuildAsync(Guid id, bool succeeded, DateTimeOffset finishedAt, int attempts, string summary, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_completeSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = succeeded ? "succeeded" : "failed" });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = finishedAt });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = attempts });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Truncate(summary) });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> FailRunningBuildsAsync(DateTimeOffset now, string summary, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_failRunningSql);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = Truncate(summary) });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static string Truncate(string summary) => summary.Length <= 500 ? summary : summary[..500];

    private static SelectionState MapSelection(NpgsqlDataReader r, int o)
    {
        var modules = r.GetFieldValue<string[]>(o + 2);
        var approvals = r.GetFieldValue<Guid[]>(o + 3);
        var pending = new Dictionary<string, Guid>(StringComparer.Ordinal);
        for (var i = 0; i < modules.Length; i++)
            pending[modules[i]] = approvals[i];
        return new SelectionState(
            Revision: r.GetInt64(o),
            SelectedModuleIds: r.GetFieldValue<string[]>(o + 1),
            PendingApprovals: pending,
            UpdatedBy: r.IsDBNull(o + 4) ? null : r.GetGuid(o + 4),
            UpdatedAt: r.IsDBNull(o + 5) ? null : r.GetFieldValue<DateTimeOffset>(o + 5));
    }

    private static BuildRow MapBuild(NpgsqlDataReader r) => new(
        Id: r.GetGuid(0),
        SelectionRevision: r.GetInt64(1),
        Status: r.GetString(2),
        StartedBy: r.GetGuid(3),
        StartedAt: r.GetFieldValue<DateTimeOffset>(4),
        FinishedAt: r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        IdempotencyKey: r.GetString(6),
        Attempts: r.GetInt32(7),
        ResultSummary: r.IsDBNull(8) ? null : r.GetString(8));
}
