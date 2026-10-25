using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Providers.Search.PgFts;

/// <summary>A per-class database with the common migrations applied (search_document is migration 0007).</summary>
public sealed class SearchDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var source = new EmbeddedMigrationSource(typeof(Bazaar.Server.Providers.Search.PgFts.PgFtsSearchProvider).Assembly);
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
