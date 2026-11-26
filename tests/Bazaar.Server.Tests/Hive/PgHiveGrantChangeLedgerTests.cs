using Bazaar.Server.Hive.Abstractions;
using Bazaar.Server.Hive.Approvals;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Hive;

/// <summary>The Postgres pending-change ledger: changes survive a new ledger instance (a restart), and removal forgets them.</summary>
public sealed class PgHiveGrantChangeLedgerTests(HiveDb db) : IClassFixture<HiveDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<PgHiveGrantChangeLedger> FreshAsync()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        await db.ClearAsync();
        return new PgHiveGrantChangeLedger(db.DataSource);
    }

    [Fact]
    public async Task A_change_survives_a_new_ledger_instance_and_a_repeat_adds_nothing()
    {
        var ledger = await FreshAsync();
        var id = Guid.NewGuid();
        var change = new HiveGrantChange(HiveGrantAction.Revoke, "alpha", "beta", HiveKind.Failure);

        await ledger.AddAsync(id, change, Ct);
        await ledger.AddAsync(id, change, Ct);

        var (ticket, loaded) = Assert.Single(await new PgHiveGrantChangeLedger(db.DataSource).SnapshotAsync(Ct));
        Assert.Equal(id, ticket);
        Assert.Equal(change, loaded);
    }

    [Fact]
    public async Task Remove_forgets_the_change()
    {
        var ledger = await FreshAsync();
        var id = Guid.NewGuid();
        await ledger.AddAsync(id, new HiveGrantChange(HiveGrantAction.Set, "a", "b", HiveKind.Procedure), Ct);

        await ledger.RemoveAsync(id, Ct);

        Assert.Empty(await ledger.SnapshotAsync(Ct));
    }
}
