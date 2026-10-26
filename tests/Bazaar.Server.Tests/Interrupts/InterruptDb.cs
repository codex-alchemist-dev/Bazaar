using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Interrupts;

/// <summary>
/// A per-class database with only the interrupt frame migration (0015) applied. 0015 has no foreign keys, so it runs
/// without the cluster-wide roles of 0001.
/// </summary>
public sealed class InterruptDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var files = new EmbeddedMigrationSource(typeof(Bazaar.Server.Interrupts.InterruptStackService).Assembly).Load(MigrationRunner.Common);
        var source = new InMemoryMigrationSource(files.Where(f => f.Version == 15));
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
