using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Interrupts;

/// <summary>
/// A per-class database with the interrupt frame migrations (0015, 0019) applied. 0015 has no foreign keys, so it runs
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
        // 0015 creates the table with a plaintext reason; 0019 seals it. Both run in order, as on a real install.
        var source = new InMemoryMigrationSource(files.Where(f => f.Version is 15 or 19));
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
