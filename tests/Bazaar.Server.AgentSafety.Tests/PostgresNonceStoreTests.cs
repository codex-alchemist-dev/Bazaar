using System.Security.Cryptography;
using Bazaar.Server.AgentSafety.Persistence;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.AgentSafety.Tests;

public sealed class PostgresNonceStoreTests
{
    [Fact]
    public async Task A_pending_nonce_is_consumed_once_and_not_again_after_a_restart()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("nonce", ServerMigrations.MigrateAsync, ct);
        var nonce = RandomNumberGenerator.GetBytes(16);
        var store = new PostgresNonceStore(db.DataSource, TimeProvider.System);
        await store.AddAsync(new PendingNonce(nonce, "agent-a", "device-1", DateTimeOffset.UtcNow.AddMinutes(2)), ct);

        var first = await store.TryConsumeAsync(nonce, ct);
        var afterRestart = await new PostgresNonceStore(db.DataSource, TimeProvider.System).TryConsumeAsync(nonce, ct);

        Assert.NotNull(first);
        Assert.Equal("agent-a", first.AgentId);
        Assert.Null(afterRestart);
    }

    [Fact]
    public async Task One_device_cannot_hold_more_than_the_cap_of_pending_nonces_and_other_devices_are_unaffected()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var ct = TestContext.Current.CancellationToken;
        await using var db = await PgTestDatabase.CreateAsync("nonce_cap", ServerMigrations.MigrateAsync, ct);
        var store = new PostgresNonceStore(db.DataSource, TimeProvider.System);
        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        for (var i = 0; i < PostgresNonceStore.MaxPendingPerDevice; i++)
        {
            await store.AddAsync(new PendingNonce(RandomNumberGenerator.GetBytes(16), "agent-a", "device-1", expires), ct);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.AddAsync(new PendingNonce(RandomNumberGenerator.GetBytes(16), "agent-a", "device-1", expires), ct));
        await store.AddAsync(new PendingNonce(RandomNumberGenerator.GetBytes(16), "agent-a", "device-2", expires), ct);
    }
}
