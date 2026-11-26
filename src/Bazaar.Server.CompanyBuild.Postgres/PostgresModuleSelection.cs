using System.Text.Json;
using Bazaar.Server.WebAdmin;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.CompanyBuild.Postgres;

/// <summary>
/// <see cref="IAdminModuleSelection"/> over Postgres (doc 37 section 4a, PROPOSED). The selection is one row of the settings table
/// with key <see cref="SelectionKey"/> and value <c>{"revision":N,"modules":[...]}</c>; a commit is a compare-and-set on the revision,
/// so a stale revision changes nothing and is a conflict. Before the first commit there is no row: revision 0, and every catalogue
/// module counts as selected (the shipped default). The rebuild queue is <c>module_rebuild</c>: the selection revision is the
/// idempotency key (one rebuild per revision; a repeat start returns the existing job), and at most one rebuild is queued or running.
/// The module catalogue is a snapshot the server publishes at start (<see cref="PostgresModuleCatalogue"/>).
/// </summary>
public sealed class PostgresModuleSelection(NpgsqlDataSource dataSource, TimeProvider clock) : IAdminModuleSelection, IRebuildJobs
{
    /// <summary>The settings key that holds the selection (doc 37 section 4a).</summary>
    public const string SelectionKey = "modules.selection";

    private const string _kernelTag = "kernel";

    private const string _selectSql = "SELECT value::text FROM setting WHERE key = $1 AND scope = 'install' AND scope_id = ''";

    private const string _insertSql =
        "INSERT INTO setting (key, scope, scope_id, value, updated_at, updated_by) VALUES ($1, 'install', '', $2::jsonb, $3, $4) ON CONFLICT DO NOTHING";

    private const string _updateSql =
        "UPDATE setting SET value = $2::jsonb, updated_at = $3, updated_by = $4 " +
        "WHERE key = $1 AND scope = 'install' AND scope_id = '' AND (value->>'revision')::bigint = $5";

    private const string _buildColumns = "build_id, state, queued_at, finished_at, report_summary, attempt";

    public async Task<AdminModuleCatalogue> GetCatalogueAsync(CancellationToken ct = default)
    {
        var modules = await ReadCatalogueAsync(ct).ConfigureAwait(false);
        var (revision, selected) = await ReadSelectionAsync(ct).ConfigureAwait(false);
        var infos = modules
            .Select(m => new AdminModuleInfo(m.Id, m.Name, m.Version, m.Tag, m.DependsOn, selected is null || selected.Contains(m.Id)))
            .ToList();
        return new AdminModuleCatalogue(revision, infos);
    }

    public async Task<IReadOnlyList<AdminBuildRecord>> GetBuildsAsync(CancellationToken ct = default)
    {
        var builds = new List<AdminBuildRecord>();
        await using var cmd = dataSource.CreateCommand($"SELECT {_buildColumns} FROM module_rebuild ORDER BY queued_at DESC, selection_revision DESC LIMIT 50");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            builds.Add(ReadBuild(reader));
        }

        return builds;
    }

    public async Task<AdminSelectionPreview> PreviewAsync(IReadOnlyList<string> selectedModuleIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedModuleIds);
        var modules = await ReadCatalogueAsync(ct).ConfigureAwait(false);
        var (_, current) = await ReadSelectionAsync(ct).ConfigureAwait(false);
        var wanted = new HashSet<string>(selectedModuleIds, StringComparer.Ordinal);
        var problems = Validate(modules, wanted);

        var effectiveCurrent = current ?? [.. modules.Select(m => m.Id)];
        var added = wanted.Where(id => !effectiveCurrent.Contains(id)).Order(StringComparer.Ordinal).ToList();
        var removed = effectiveCurrent.Where(id => !wanted.Contains(id)).Order(StringComparer.Ordinal).ToList();
        return new AdminSelectionPreview(problems.Count == 0, problems, added, removed);
    }

    public async Task<AdminSelectionCommit> CommitAsync(Guid actorPrincipalId, IReadOnlyList<string> selectedModuleIds, long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(selectedModuleIds);
        var modules = await ReadCatalogueAsync(ct).ConfigureAwait(false);
        var wanted = new HashSet<string>(selectedModuleIds, StringComparer.Ordinal);
        var problems = Validate(modules, wanted);
        var (revision, _) = await ReadSelectionAsync(ct).ConfigureAwait(false);
        if (problems.Count > 0)
        {
            return new AdminSelectionCommit(false, revision, problems);
        }

        var next = expectedRevision + 1;
        var json = JsonSerializer.Serialize(new StoredSelection(next, [.. wanted.Order(StringComparer.Ordinal)]));
        var now = clock.GetUtcNow();
        var actor = actorPrincipalId.ToString("D");

        int rows;
        if (expectedRevision == 0)
        {
            // No row yet: whoever inserts first wins; a loser sees zero rows and the revision the winner wrote.
            await using var insert = dataSource.CreateCommand(_insertSql);
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SelectionKey });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = json });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor });
            rows = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else
        {
            await using var update = dataSource.CreateCommand(_updateSql);
            update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SelectionKey });
            update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = json });
            update.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
            update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = actor });
            update.Parameters.Add(new NpgsqlParameter<long> { TypedValue = expectedRevision });
            rows = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (rows == 1)
        {
            return new AdminSelectionCommit(true, next, []);
        }

        var (current, _) = await ReadSelectionAsync(ct).ConfigureAwait(false);
        return new AdminSelectionCommit(false, current, [new AdminSelectionProblem("", "The selection changed since it was read (stale revision).")]);
    }

    public async Task<AdminRebuildStart> StartRebuildAsync(Guid actorPrincipalId, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        await using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // One lock for the whole start, so two callers cannot both see "nothing active" and both insert.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('module_rebuild'))", connection, tx))
        {
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // The same key returns the first result, whatever the revision has done since.
        if (await FindAsync(connection, tx, "idempotency_key = $1", new NpgsqlParameter<string> { TypedValue = idempotencyKey }, ct).ConfigureAwait(false) is { } first)
        {
            return new AdminRebuildStart(true, first.BuildId, null);
        }

        long revision;
        await using (var revCmd = new NpgsqlCommand(_selectSql, connection, tx))
        {
            revCmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SelectionKey });
            revision = ParseSelection(await revCmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string).Revision;
        }

        if (revision == 0)
        {
            return new AdminRebuildStart(false, null, "No module selection has been committed yet.");
        }

        if (await FindAsync(connection, tx, "selection_revision = $1", new NpgsqlParameter<long> { TypedValue = revision }, ct).ConfigureAwait(false) is { } existing)
        {
            // One rebuild per revision: a repeat start returns the existing job. A failed one is not retried here; a new revision is.
            return existing.State == "failed"
                ? new AdminRebuildStart(false, existing.BuildId, "The rebuild for this selection revision failed; commit a new revision to build again.")
                : new AdminRebuildStart(true, existing.BuildId, null);
        }

        if (await FindAsync(connection, tx, "state IN ('queued', 'running')", null, ct).ConfigureAwait(false) is { } active)
        {
            return new AdminRebuildStart(false, active.BuildId, "A rebuild is already running.");
        }

        var buildId = Guid.NewGuid();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO module_rebuild (build_id, selection_revision, idempotency_key, state, requested_by, queued_at) VALUES ($1, $2, $3, 'queued', $4, $5)",
            connection, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = buildId });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = revision });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = idempotencyKey });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = actorPrincipalId });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = clock.GetUtcNow() });
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new AdminRebuildStart(true, buildId.ToString("D"), null);
    }

    public async Task<RebuildJob?> ClaimNextAsync(CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE module_rebuild SET state = 'running', started_at = $1, attempt = attempt + 1 " +
            "WHERE build_id = (SELECT build_id FROM module_rebuild WHERE state = 'queued' ORDER BY queued_at LIMIT 1 FOR UPDATE SKIP LOCKED) " +
            "RETURNING build_id, selection_revision");
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = clock.GetUtcNow() });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var buildId = reader.GetGuid(0);
        var revision = reader.GetInt64(1);
        await reader.CloseAsync().ConfigureAwait(false);
        var (_, selected) = await ReadSelectionAsync(ct).ConfigureAwait(false);
        return new RebuildJob(buildId.ToString("D"), revision, [.. selected ?? []]);
    }

    public async Task<bool> CompleteAsync(string buildId, bool succeeded, string? reportSummary, CancellationToken ct = default)
    {
        if (!Guid.TryParse(buildId, out var id))
        {
            return false;
        }

        await using var cmd = dataSource.CreateCommand(
            "UPDATE module_rebuild SET state = $2, finished_at = $3, report_summary = $4 WHERE build_id = $1 AND state = 'running'");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = succeeded ? "succeeded" : "failed" });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = clock.GetUtcNow() });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)reportSummary ?? DBNull.Value });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static List<AdminSelectionProblem> Validate(IReadOnlyList<CatalogueModule> modules, HashSet<string> wanted)
    {
        var problems = new List<AdminSelectionProblem>();
        var known = modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        foreach (var id in wanted.Order(StringComparer.Ordinal))
        {
            if (!known.ContainsKey(id))
            {
                problems.Add(new AdminSelectionProblem(id, "Unknown module."));
            }
        }

        foreach (var module in modules.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (module.Tag == _kernelTag && !wanted.Contains(module.Id))
            {
                problems.Add(new AdminSelectionProblem(module.Id, "A kernel module cannot be deselected."));
            }

            if (!wanted.Contains(module.Id))
            {
                continue;
            }

            foreach (var dependency in module.DependsOn)
            {
                if (!wanted.Contains(dependency))
                {
                    problems.Add(new AdminSelectionProblem(module.Id, $"Needs '{dependency}', which is not selected."));
                }
            }
        }

        return problems;
    }

    private async Task<(long Revision, HashSet<string>? Selected)> ReadSelectionAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_selectSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = SelectionKey });
        var stored = ParseSelection(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string);
        return (stored.Revision, stored.Revision == 0 ? null : new HashSet<string>(stored.Modules, StringComparer.Ordinal));
    }

    private static StoredSelection ParseSelection(string? json) =>
        json is null ? new StoredSelection(0, []) : JsonSerializer.Deserialize<StoredSelection>(json) ?? new StoredSelection(0, []);

    private async Task<List<CatalogueModule>> ReadCatalogueAsync(CancellationToken ct)
    {
        var list = new List<CatalogueModule>();
        await using var cmd = dataSource.CreateCommand("SELECT module_id, name, version, tag, depends_on FROM module_catalogue ORDER BY module_id");
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new CatalogueModule(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<string[]>(4)));
        }

        return list;
    }

    private static async Task<(string BuildId, string State)?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string where, NpgsqlParameter? parameter, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"SELECT build_id, state FROM module_rebuild WHERE {where} LIMIT 1", connection, tx);
        if (parameter is not null)
        {
            cmd.Parameters.Add(parameter);
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? (reader.GetGuid(0).ToString("D"), reader.GetString(1)) : null;
    }

    private static AdminBuildRecord ReadBuild(NpgsqlDataReader reader) => new(
        reader.GetGuid(0).ToString("D"),
        reader.GetString(1),
        reader.GetFieldValue<DateTimeOffset>(2),
        reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetInt32(5));

    private sealed record StoredSelection(
        [property: System.Text.Json.Serialization.JsonPropertyName("revision")] long Revision,
        [property: System.Text.Json.Serialization.JsonPropertyName("modules")] string[] Modules);

    private sealed record CatalogueModule(string Id, string Name, string Version, string Tag, string[] DependsOn);
}
