using System.Reflection;
using Cdax.Core;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.WhatIf.Tests;

public sealed class WhatIfStoreAndComponentTests
{
    private static WhatIfRun SampleRun() => new(
        Guid.NewGuid(), "decision-1", Guid.NewGuid(), 9, MetricSpec.Default.Hash(),
        [
            new WhatIfBranchRun("a", ["t1", "t2"], WhatIfRunState.Completed,
                new WhatIfBranchResult(new BranchMeasurement(true, 12, ReviewBand.Medium, 3000), 0.75m, MetricSpec.Default.Hash(), "sandbox/x/a")),
            new WhatIfBranchRun("b", ["t3"], WhatIfRunState.Failed, AbortReason: "TimeoutException"),
        ],
        new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task The_in_memory_store_round_trips_a_run()
    {
        var store = new InMemoryWhatIfRunStore();
        var run = SampleRun();

        await store.SaveAsync(run, TestContext.Current.CancellationToken);

        Assert.Equal(run, await store.GetAsync(run.Id, TestContext.Current.CancellationToken));
        Assert.Null(await store.GetAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_run_state_is_derived_from_its_branches()
    {
        Assert.Equal(WhatIfRunState.Failed, SampleRun().State);
        Assert.Equal(WhatIfRunState.Aborted, (SampleRun() with { Branches = [new WhatIfBranchRun("a", ["t"], WhatIfRunState.Aborted, AbortReason: "r")] }).State);
    }

    [Fact]
    public async Task The_postgres_store_round_trips_a_run_and_replaces_it_on_save()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("whatif", MigrateAsync, ct);
        var store = new PostgresWhatIfRunStore(db.DataSource);
        var run = SampleRun();

        await store.SaveAsync(run, ct);
        var loaded = await store.GetAsync(run.Id, ct);
        Assert.NotNull(loaded);
        Assert.Equal(run.MetricsSpecHash, loaded.MetricsSpecHash);
        Assert.Equal(["a", "b"], loaded.Branches.Select(b => b.Name));
        Assert.Equal(run.Branches[0].Result, loaded.Branches[0].Result);
        Assert.Equal(["t1", "t2"], loaded.Branches[0].Tasks);

        var updated = run with { Branches = [run.Branches[0], run.Branches[1] with { State = WhatIfRunState.Completed, AbortReason = null }] };
        await store.SaveAsync(updated, ct);
        Assert.Equal(WhatIfRunState.Completed, (await store.GetAsync(run.Id, ct))!.State);
    }

    [Fact]
    public void The_component_manifest_follows_the_id_and_migration_rules()
    {
        var component = new WhatIfComponent();

        Assert.Equal("what-if", component.Manifest.Id);
        Assert.Equal("what_if", component.Manifest.MigrationNamespace);
        Assert.Contains(typeof(WhatIfComponent).Assembly.GetManifestResourceNames(), n => n.EndsWith(".Sql.components.what_if.0001_what_if_run.sql", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_component_wires_a_working_engine_over_the_fakes_with_weight_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [WhatIfComponent.ReputationWeightConfigKey] = "0.5" })
            .Build();
        var services = new ServiceCollection();
        new WhatIfComponent().AddServices(services, configuration);
        services.AddBazaarWhatIfFakes();
        await using var provider = services.BuildServiceProvider();

        var result = await provider.GetRequiredService<WhatIfEngine>().RunAsync(
            new WhatIfRequest("d", Guid.NewGuid(), 1, MetricSpec.Default, [new WhatIfBranch("a", ["t"]), new WhatIfBranch("b", ["t"])]),
            TestContext.Current.CancellationToken);

        Assert.Equal(WhatIfRunState.Completed, result.State);
        Assert.Equal(0.5m, result.ReputationWeight);
    }

    [Fact]
    public void An_out_of_range_configured_weight_is_refused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [WhatIfComponent.ReputationWeightConfigKey] = "2" })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddBazaarWhatIf(configuration));
    }

    // Applies the module's own embedded SQL. The server's migration runner is not referenced (the module never depends on Bazaar.Server).
    private static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken ct)
    {
        var assembly = typeof(WhatIfComponent).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await using var cmd = dataSource.CreateCommand(await reader.ReadToEndAsync(ct));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
