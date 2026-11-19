using Bazaar.Approvals;
using Bazaar.Approvals.Db;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Security.Approvals.Db;

/// <summary>The durable owner nonce store (doc 32 section 5a): a nonce is spent once, even across store instances.</summary>
public sealed class PostgresOwnerNonceStoreTests
{
    private static readonly DateTimeOffset _expires = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private static byte[] Nonce(byte seed) => Enumerable.Range(0, 16).Select(i => (byte)(seed + i)).ToArray();

    [Fact]
    public async Task A_nonce_is_spent_once_even_through_a_new_store_instance()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var first = new PostgresOwnerNonceStore(db.DataSource);

        Assert.True(await first.TryUseAsync(Nonce(1), _expires, Ct));
        Assert.True(await new PostgresOwnerNonceStore(db.DataSource).TryUseAsync(Nonce(2), _expires, Ct));
        Assert.False(await new PostgresOwnerNonceStore(db.DataSource).TryUseAsync(Nonce(1), _expires, Ct));
    }

    [Fact]
    public async Task Thirty_two_parallel_spends_of_one_nonce_across_store_instances_let_exactly_one_through()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);

        var spends = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => new PostgresOwnerNonceStore(db.DataSource).TryUseAsync(Nonce(7), _expires, Ct), Ct)));

        Assert.Equal(1, spends.Count(s => s));
    }

    [Fact]
    public async Task Purge_removes_only_expired_nonces()
    {
        SkipWithoutDatabase();
        await using var db = await ApprovalTestDb.CreateAsync(Ct);
        var store = new PostgresOwnerNonceStore(db.DataSource);
        await store.TryUseAsync(Nonce(1), _expires.AddHours(-1), Ct);
        await store.TryUseAsync(Nonce(2), _expires, Ct);

        Assert.Equal(1, await store.PurgeExpiredAsync(_expires.AddMinutes(-30), Ct));
        Assert.True(await store.TryUseAsync(Nonce(1), _expires, Ct));
    }
}
