using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Setup.Postgres.Tests;

public sealed class PostgresBootstrapCodeStoreTests
{
    private const string _skip = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    [Fact]
    public async Task A_code_works_once_and_only_its_digest_is_stored()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var store = new PostgresBootstrapCodeStore(db.DataSource);

        var code = await store.CreateAsync(ct);

        Assert.False(await store.ConsumeAsync("wrong", ct));
        Assert.True(await store.ConsumeAsync(code, ct));
        Assert.False(await store.ConsumeAsync(code, ct));
        await using var cmd = db.DataSource.CreateCommand("SELECT count(*) FROM setup_bootstrap_code WHERE encode(digest, 'escape') = $1");
        cmd.Parameters.AddWithValue(code);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task A_new_code_replaces_the_old_one_and_revoke_removes_it()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var store = new PostgresBootstrapCodeStore(db.DataSource);

        var first = await store.CreateAsync(ct);
        var second = await store.CreateAsync(ct);

        Assert.NotEqual(first, second);
        Assert.False(await store.ConsumeAsync(first, ct));
        await store.RevokeAsync(ct);
        Assert.False(await store.ConsumeAsync(second, ct));
    }
}
