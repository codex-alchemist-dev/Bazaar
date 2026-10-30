using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Org;

/// <summary>Migration 0018 applies to an empty database and re-applies as a no-op (doc 04 runner rules).</summary>
public sealed class OrgMigrationTests(OrgBareDb db) : IClassFixture<OrgBareDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_applies_once_then_reapplies_as_a_no_op()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var runner = new MigrationRunner(db.DataSource);
        var source = OrgTestMigrations.Org18();

        var first = await runner.MigrateAsync(source, MigrationRunner.Common, Ct);
        var second = await runner.MigrateAsync(source, MigrationRunner.Common, Ct);

        var applied = Assert.Single(first.Applied);
        Assert.Equal(18, applied.Version);
        Assert.Equal("org", applied.Name);
        Assert.Equal(0, first.AlreadyApplied);
        Assert.Empty(second.Applied);
        Assert.Equal(1, second.AlreadyApplied);
    }
}
