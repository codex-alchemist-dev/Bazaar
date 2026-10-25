using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Jobs;

/// <summary>A per-class database with the common migrations applied (the job table is migration 0006).</summary>
public sealed class JobDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var source = new EmbeddedMigrationSource(typeof(Bazaar.Server.Jobs.JobStore).Assembly);
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
