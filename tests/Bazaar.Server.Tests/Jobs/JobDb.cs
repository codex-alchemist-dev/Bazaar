using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Jobs;

/// <summary>
/// A per-class database with only the job table migration (0006) applied. Migration 0001 creates cluster-wide roles, so it
/// cannot run again in every per-class database; 0006 has no foreign keys and needs nothing else.
/// </summary>
public sealed class JobDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var files = new EmbeddedMigrationSource(typeof(Bazaar.Server.Jobs.JobStore).Assembly).Load(MigrationRunner.Common);
        var source = new InMemoryMigrationSource(files.Where(f => f.Version == 6));
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
