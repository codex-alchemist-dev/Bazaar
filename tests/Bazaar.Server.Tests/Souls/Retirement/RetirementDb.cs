using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Souls.Retirement;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Souls.Retirement;

public sealed class RetirementDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        const string ns = "souls";
        var files = new EmbeddedMigrationSource(typeof(PostgresRetirementLedger).Assembly).Load(ns);
        await new MigrationRunner(DataSource).MigrateAsync(new InMemoryMigrationSource(files), ns);
    }
}
