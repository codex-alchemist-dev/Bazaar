using Bazaar.Server.Setup.Postgres;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Setup.Postgres.Tests;

public sealed class PostgresInstallIdProviderTests
{
    private const string _skip = "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.";

    [Fact]
    public async Task The_id_is_created_once_and_every_call_returns_it()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var first = await new PostgresInstallIdProvider(db.DataSource).GetOrCreateAsync(ct);
        var again = await new PostgresInstallIdProvider(db.DataSource).GetOrCreateAsync(ct);

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, again);
    }

    [Fact]
    public async Task Racing_callers_all_see_one_id()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, _skip);
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SetupDatabase.CreateAsync(ct);
        var provider = new PostgresInstallIdProvider(db.DataSource);

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => provider.GetOrCreateAsync(ct)));

        Assert.Single(ids.Distinct());
    }
}
