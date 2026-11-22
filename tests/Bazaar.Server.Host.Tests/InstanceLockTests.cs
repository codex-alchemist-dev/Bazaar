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

    /// <summary>Chaos finding F1: terminating the lock's backend (as a Postgres restart does) used to leave IsHeld true with no lock.</summary>
    private async Task TerminateLockBackendAsync()
    {
        await using var admin = new Npgsql.NpgsqlConnection(db.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND granted AND objid = $1::oid AND classid = $2::oid",
            admin)
        {
            Parameters = { new() { Value = (long)(uint)(PostgresInstanceLock.Key & 0xFFFFFFFF) }, new() { Value = (long)(uint)(PostgresInstanceLock.Key >> 32) } },
        };
        Assert.NotNull(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Verify_reacquires_a_lock_lost_when_its_backend_is_terminated()
    {
        SkipWithoutDatabase();
        await using var connection = NewConnection();
        await using var instanceLock = new PostgresInstanceLock(connection);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await instanceLock.TryAcquireAsync(ct));

        await TerminateLockBackendAsync();

        Assert.Equal(InstanceLockStatus.Held, await instanceLock.VerifyAsync(ct));
        Assert.True(instanceLock.IsHeld);
        await using var rival = new PostgresInstanceLock(NewConnection());
        Assert.False(await rival.TryAcquireAsync(ct));
    }

    [Fact]
    public async Task Watchdog_stops_the_host_when_another_instance_took_the_lost_lock()
    {
        SkipWithoutDatabase();
        await using var connection = NewConnection();
        await using var instanceLock = new PostgresInstanceLock(connection);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await instanceLock.TryAcquireAsync(ct));
        await TerminateLockBackendAsync();
        await using var rival = new PostgresInstanceLock(NewConnection());
        Assert.True(await rival.TryAcquireAsync(ct));

        var state = new HostState(TimeProvider.System);
        state.MarkStarted();
        var exit = new HostExitCode();
        var lifetime = new RecordingLifetime();
        var watchdog = new InstanceLockWatchdog(
            instanceLock, state, exit, lifetime, Microsoft.Extensions.Logging.Abstractions.NullLogger<InstanceLockWatchdog>.Instance);

        Assert.False(await watchdog.CheckOnceAsync(ct));
        Assert.Equal(1, exit.Value);
        Assert.True(lifetime.Stopped);
        Assert.False(instanceLock.IsHeld);
        Assert.False((await new InstanceLockReadinessCheck(instanceLock).CheckAsync(ct)).Ok);
    }

    private sealed class RecordingLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopped = true;
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
