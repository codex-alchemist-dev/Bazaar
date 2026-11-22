using Npgsql;

namespace Bazaar.Server.Health;

/// <summary>Postgres <see cref="IHealthReportStore"/> over <c>health_reports</c> (migration components/health/0002, doc 25).</summary>
public sealed class PgHealthReportStore(NpgsqlDataSource dataSource) : IHealthReportStore
{
    public async Task PutAsync(HealthReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO health_reports (project_id, kind, toolchain, day, source_ref, content)
            VALUES (@project, @kind, @toolchain, @day, @source, @content)
            ON CONFLICT (project_id, kind, toolchain, day, source_ref)
            DO UPDATE SET content = EXCLUDED.content, received_at = now()
            """);
        cmd.Parameters.AddWithValue("project", report.ProjectId);
        cmd.Parameters.AddWithValue("kind", report.Kind);
        cmd.Parameters.AddWithValue("toolchain", report.Toolchain);
        cmd.Parameters.AddWithValue("day", report.Day);
        cmd.Parameters.AddWithValue("source", report.SourceRef);
        cmd.Parameters.AddWithValue("content", report.Content);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HealthReport>> ListAsync(string projectId, DateOnly day, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        await using var cmd = dataSource.CreateCommand("""
            SELECT kind, toolchain, source_ref, content
            FROM health_reports
            WHERE project_id = @project AND day = @day
            ORDER BY kind COLLATE "C", toolchain COLLATE "C", source_ref COLLATE "C"
            """);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("day", day);
        var reports = new List<HealthReport>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            reports.Add(new HealthReport(projectId, reader.GetString(0), reader.GetString(1), day, reader.GetString(2), reader.GetString(3)));
        return reports;
    }

    public async Task<IReadOnlyList<string>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT project_id FROM (SELECT DISTINCT project_id FROM health_reports) p ORDER BY project_id COLLATE \"C\"");
        var projects = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            projects.Add(reader.GetString(0));
        return projects;
    }

    public async Task<int> PruneBeforeAsync(DateOnly cutoff, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM health_reports WHERE day < @cutoff");
        cmd.Parameters.AddWithValue("cutoff", cutoff);
        return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
