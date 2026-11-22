using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Setup.Postgres.Tests;

public sealed class PostgresSetupInstanceLockTests
{
    [Fact]
    public async Task A_second_acquire_is_refused_while_the_first_lease_is_held()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var first = new PostgresSetupInstanceLock(db.DataSource);
        var second = new PostgresSetupInstanceLock(db.DataSource);

        await using var lease = await first.AcquireAsync(ct);

        await Assert.ThrowsAsync<SetupInstanceLockHeldException>(() => second.AcquireAsync(ct));
    }

    [Fact]
    public async Task Disposing_the_lease_releases_the_lock_for_the_next_apply()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var gate = new PostgresSetupInstanceLock(db.DataSource);

        var lease = await gate.AcquireAsync(ct);
        await lease.DisposeAsync();
        await lease.DisposeAsync(); // a second dispose is harmless

        await using var again = await gate.AcquireAsync(ct);
        Assert.NotNull(again);
    }

    [Fact]
    public void The_key_is_not_the_running_host_key()
    {
        // The host holds BAZINST1 for its whole life, so setup must use its own key or an apply inside or beside the host could never run.
        Assert.NotEqual(0x42415A494E535431, PostgresSetupInstanceLock.Key);
        Assert.Equal(0x42415A5345545550, PostgresSetupInstanceLock.Key);
    }
}
