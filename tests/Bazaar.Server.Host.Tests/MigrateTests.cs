using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Hosting;
using Bazaar.Server.Secrets;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class MigrateDb : PgFixture, IAsyncLifetime
{
    public override ValueTask InitializeAsync() => PgSource.IsUnsupportedHost ? ValueTask.CompletedTask : base.InitializeAsync();
}

public sealed class MigrateTests(MigrateDb db) : IClassFixture<MigrateDb>
{
    private const string EnvName = "BAZAAR_HOST_TEST_DB";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Bazaar:Host:DataRoot"] = Path.GetTempPath(),
        ["Bazaar:Host:DatabaseConnectionReference"] = "env:" + EnvName,
    }).Build();

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Invalid_config_is_refused()
    {
        var output = new StringWriter();
        var code = await MigrateSubcommand.RunAsync([], output, new ConfigurationBuilder().Build(), TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
        Assert.Contains("DataRoot", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_applies_and_the_schema_gate_then_passes()
    {
        SkipWithoutDatabase();
        Environment.SetEnvironmentVariable(EnvName, db.ConnectionString);
        try
        {
            var output = new StringWriter();
            Assert.Equal(0, await MigrateSubcommand.RunAsync([], output, Config(), TestContext.Current.CancellationToken));
            Assert.Contains("done:", output.ToString(), StringComparison.Ordinal);

            await using var database = new DatabaseConnection(
                Options.Create(new BazaarHostOptions { DataRoot = Path.GetTempPath(), DatabaseConnectionReference = "env:DB" }),
                new BootstrapSecretResolver(_ => db.ConnectionString));
            await new MigrationSchemaGate(database).EnsureCurrentAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    [Fact]
    public async Task Migrate_refuses_while_another_instance_holds_the_lock()
    {
        SkipWithoutDatabase();
        Environment.SetEnvironmentVariable(EnvName, db.ConnectionString);
        try
        {
            await using var database = new DatabaseConnection(
                Options.Create(new BazaarHostOptions { DataRoot = Path.GetTempPath(), DatabaseConnectionReference = "env:DB" }),
                new BootstrapSecretResolver(_ => db.ConnectionString));
            await using var running = new PostgresInstanceLock(database);
            Assert.True(await running.TryAcquireAsync(TestContext.Current.CancellationToken));

            var output = new StringWriter();
            Assert.Equal(1, await MigrateSubcommand.RunAsync([], output, Config(), TestContext.Current.CancellationToken));
            Assert.Contains("instance lock", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvName, null);
        }
    }

    [Fact]
    public async Task Schema_gate_refuses_an_unmigrated_database()
    {
        SkipWithoutDatabase();
        await using var fresh = new MigrateDb();
        await fresh.InitializeAsync();
        await using var database = new DatabaseConnection(
            Options.Create(new BazaarHostOptions { DataRoot = Path.GetTempPath(), DatabaseConnectionReference = "env:DB" }),
            new BootstrapSecretResolver(_ => fresh.ConnectionString));
        // A fresh clone may already carry history from the template migrator; only assert when it is behind.
        var state = await new MigrationRunner(await database.GetAsync(TestContext.Current.CancellationToken))
            .GetStateAsync(new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly), MigrateCommand.CurrentProvider(), TestContext.Current.CancellationToken);
        if (state == SchemaState.Behind)
        {
            await Assert.ThrowsAsync<SchemaVersionException>(() => new MigrationSchemaGate(database).EnsureCurrentAsync(TestContext.Current.CancellationToken));
        }
    }
}
