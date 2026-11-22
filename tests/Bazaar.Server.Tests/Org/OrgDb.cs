using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Org;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Org;

/// <summary>Migration files for the org tests. Only 0018 is applied: it has no foreign keys, so it runs on an empty database.</summary>
internal static class OrgTestMigrations
{
    public static InMemoryMigrationSource Org18() =>
        new(new EmbeddedMigrationSource(typeof(OrgRelation).Assembly).Load(MigrationRunner.Common).Where(f => f.Version == 18));
}

/// <summary>A per-class database with migration 0018 applied, for the constraint and repository tests.</summary>
public sealed class OrgDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        await new MigrationRunner(DataSource).MigrateAsync(OrgTestMigrations.Org18(), MigrationRunner.Common);
    }
}

/// <summary>A per-class database with nothing applied, for the migration apply and re-apply test.</summary>
public sealed class OrgBareDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
    }
}
