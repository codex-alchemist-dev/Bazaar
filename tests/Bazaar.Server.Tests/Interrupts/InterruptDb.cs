using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Interrupts;

/// <summary>
/// A per-class database with the interrupt frame migrations (common 0015, interrupts 0001) applied. 0015 has no foreign keys, so it runs
/// without the cluster-wide roles of 0001.
/// </summary>
public sealed class InterruptDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var embedded = new EmbeddedMigrationSource(typeof(Bazaar.Server.Interrupts.InterruptStackService).Assembly);
        // common 0015 creates the table with a plaintext reason; the interrupts namespace 0001 seals it. Both run in order, as on a real install.
        var files = embedded.Load(MigrationRunner.Common).Where(f => f.Version == 15)
            .Concat(embedded.Load("interrupts").Where(f => f.Version == 1));
        var source = new InMemoryMigrationSource(files);
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
