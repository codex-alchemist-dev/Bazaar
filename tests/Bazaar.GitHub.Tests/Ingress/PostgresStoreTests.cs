using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Ingress;
using Bazaar.Testing.Postgres;

namespace Bazaar.GitHub.Tests.Ingress;

/// <summary>Real Postgres: the delivery dedupe and the rate state survive a new store instance (doc 21, Testing).</summary>
public sealed class PostgresStoreTests
{
    private static async Task MigrateAsync(Npgsql.NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(GitHubComponent).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0001_github_tables.sql", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task A_delivery_is_processed_once_across_store_instances_and_concurrent_marks()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghdelivery", MigrateAsync, ct);
        var store = new PgGitHubDeliveryStore(db.DataSource);
        var now = DateTimeOffset.UtcNow;

        Assert.False(await store.IsProcessedAsync("poll", "e1", ct));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => store.MarkProcessedAsync("poll", "e1", "pull_request", now, ct)));

        Assert.True(await new PgGitHubDeliveryStore(db.DataSource).IsProcessedAsync("poll", "e1", ct));
        Assert.False(await store.IsProcessedAsync("webhook", "e1", ct));
        await using var count = db.DataSource.CreateCommand("SELECT count(*) FROM github_delivery");
        Assert.Equal(1L, await count.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Rate_state_round_trips_and_upserts()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "No test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("ghrate", MigrateAsync, ct);
        var state = new PgGitHubRateState(db.DataSource);
        var reset = new DateTimeOffset(2026, 10, 30, 13, 0, 0, TimeSpan.Zero);

        Assert.Null(await state.GetAsync(7, ct));
        await state.SetAsync(7, new GitHubRateSnapshot(5000, 100, reset), ct);
        await state.SetAsync(7, new GitHubRateSnapshot(5000, 40, reset), ct);

        var read = await state.GetAsync(7, ct);
        Assert.Equal(40, read!.Remaining);
        Assert.Equal(reset, read.ResetAt);
        Assert.Null(await state.GetAsync(8, ct));
    }
}
