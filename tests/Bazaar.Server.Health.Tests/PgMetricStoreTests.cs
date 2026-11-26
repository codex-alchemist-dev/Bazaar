using System.Reflection;
using Bazaar.Server.Knowledge.Metrics;
using Bazaar.Testing.Postgres;
using Npgsql;

namespace Bazaar.Server.Health.Tests;

/// <summary>A cloned test database with the <c>health</c> component migrations applied.</summary>
public sealed class HealthDb : PgFixture, IAsyncLifetime
{
    private NpgsqlDataSource? _pool;

    public new NpgsqlDataSource DataSource => _pool ?? throw new InvalidOperationException("HealthDb is not initialised.");

    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        _pool = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(ConnectionString) { MaxPoolSize = 8 }.ConnectionString);

        var assembly = typeof(PgMetricStore).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains(".components.health.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("No health migrations are embedded in Bazaar.Server.Health.");

        foreach (var name in names)
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            await using var cmd = DataSource.CreateCommand(await reader.ReadToEndAsync());
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public async Task ClearAsync()
    {
        await using var cmd = DataSource.CreateCommand("TRUNCATE metric_samples");
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>The Postgres metric store against a real PostgreSQL, with the same expectations as the in-memory store.</summary>
public sealed class PgMetricStoreTests(HealthDb db) : IClassFixture<HealthDb>
{
    private static readonly DateOnly _day = new(2026, 11, 22);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PgMetricStore> FreshAsync()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await db.ClearAsync();
        return new PgMetricStore(db.DataSource);
    }

    private static MetricSample S(string metric, int daysAgo, double value, string dim = "", string src = "r", string project = "p") =>
        new(project, metric, dim, _day.AddDays(-daysAgo), value, src);

    [Fact]
    public async Task Upsert_counts_new_rows_and_replaces_the_value_of_an_existing_key()
    {
        var store = await FreshAsync();

        Assert.Equal(2, await store.UpsertAsync([S(MetricNames.CiRunsTotal, 0, 3), S(MetricNames.CiRunsPassed, 0, 2)], Ct));
        Assert.Equal(0, await store.UpsertAsync([S(MetricNames.CiRunsTotal, 0, 5)], Ct));

        var rows = await store.QueryAsync("p", MetricNames.CiRunsTotal, _day, _day, Ct);
        Assert.Equal(5, Assert.Single(rows).Value);
    }

    [Fact]
    public async Task Query_filters_by_project_metric_and_inclusive_range_and_orders_by_bucket()
    {
        var store = await FreshAsync();
        await store.UpsertAsync(
        [
            S(MetricNames.CoverageLinePercent, 0, 70, "dotnet"), S(MetricNames.CoverageLinePercent, 2, 80, "dotnet"),
            S(MetricNames.CoverageLinePercent, 1, 75, "luau"), S(MetricNames.CoverageLinePercent, 9, 1, "dotnet"),
            S(MetricNames.CoverageLinePercent, 0, 5, "dotnet", project: "other"),
            S(MetricNames.CiRunsTotal, 0, 1),
        ], Ct);

        var rows = await store.QueryAsync("p", MetricNames.CoverageLinePercent, _day.AddDays(-2), _day, Ct);

        Assert.Equal([80d, 75d, 70d], rows.Select(r => r.Value));
        Assert.Equal(["dotnet", "luau", "dotnet"], rows.Select(r => r.Dimension));
    }

    [Fact]
    public async Task Same_dimension_samples_with_different_sources_are_separate_rows()
    {
        var store = await FreshAsync();
        await store.UpsertAsync([S(HealthMetricNames.Reverts, 0, 1, src: "a"), S(HealthMetricNames.Reverts, 0, 2, src: "b")], Ct);

        Assert.Equal(2, (await store.QueryAsync("p", HealthMetricNames.Reverts, _day, _day, Ct)).Count);
    }

    [Fact]
    public async Task The_table_rejects_a_blank_project()
    {
        var store = await FreshAsync();

        await Assert.ThrowsAsync<PostgresException>(() => store.UpsertAsync([S(HealthMetricNames.Reverts, 0, 1, project: " ")], Ct));
    }

    [Fact]
    public async Task The_health_service_reads_through_the_postgres_store()
    {
        var store = await FreshAsync();
        await store.UpsertAsync([S(HealthMetricNames.Reverts, 0, 2), S(MetricNames.CoverageLinePercent, 1, 80, "dotnet"), S(MetricNames.CoverageLinePercent, 0, 70, "dotnet")], Ct);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        var report = await new ProjectHealthService(store, clock).GetAsync("p", Ct);

        Assert.Equal(ProjectHealthStatus.Alert, report.Status);
        Assert.Equal(2, report.TechDebt.RevertsInWindow);
    }
}
