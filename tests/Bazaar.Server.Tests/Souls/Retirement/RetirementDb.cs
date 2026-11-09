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
        var files = new EmbeddedMigrationSource(typeof(PostgresRetirementLedger).Assembly).Load(MigrationRunner.Common);
        var source = new InMemoryMigrationSource(files.Where(f => f.Version == 21));
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
