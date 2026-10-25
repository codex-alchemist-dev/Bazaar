using Bazaar.Server.Host.Startup;
using Bazaar.Server.Hosting;
using Bazaar.Server.Secrets;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public sealed class HostDb : PgFixture, IAsyncLifetime
{
    public override ValueTask InitializeAsync() => PgSource.IsUnsupportedHost ? ValueTask.CompletedTask : base.InitializeAsync();
}

/// <summary>Real-Postgres checks of the instance lock and database readiness.</summary>
public sealed class InstanceLockTests(HostDb db) : IClassFixture<HostDb>
{
    private DatabaseConnection NewConnection() => new(
        Options.Create(new BazaarHostOptions { DataRoot = Path.GetTempPath(), DatabaseConnectionReference = "env:DB" }),
        new BootstrapSecretResolver(_ => db.ConnectionString));

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    [Fact]
    public async Task Second_instance_cannot_take_the_lock_until_the_first_releases()
    {
        SkipWithoutDatabase();
        await using var connectionA = NewConnection();
        await using var connectionB = NewConnection();
        await using var first = new PostgresInstanceLock(connectionA);
        await using var second = new PostgresInstanceLock(connectionB);

        Assert.True(await first.TryAcquireAsync(TestContext.Current.CancellationToken));
        Assert.True(first.IsHeld);
        Assert.False(await second.TryAcquireAsync(TestContext.Current.CancellationToken));
        Assert.False(second.IsHeld);

        await first.DisposeAsync();

        Assert.True(await second.TryAcquireAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Database_readiness_is_ok_against_a_live_server()
    {
        SkipWithoutDatabase();
        await using var connection = NewConnection();
        var result = await new DatabaseReadinessCheck(connection).CheckAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Ok);
    }
}
