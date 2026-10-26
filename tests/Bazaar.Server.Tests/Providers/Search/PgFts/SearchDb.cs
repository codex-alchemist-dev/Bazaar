using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Providers.Search.PgFts;

/// <summary>
/// A per-class database with only the search table migration (0007) applied. Migration 0001 creates cluster-wide roles and
/// cannot run in every per-class database; 0007 has no foreign keys.
/// </summary>
public sealed class SearchDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var files = new EmbeddedMigrationSource(typeof(Bazaar.Server.Providers.Search.PgFts.PgFtsSearchProvider).Assembly)
            .Load(MigrationRunner.Common);
        var source = new InMemoryMigrationSource(files.Where(f => f.Version == 7));
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
