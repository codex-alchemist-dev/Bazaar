using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Souls;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Souls;

/// <summary>
/// A per-class database with only the soul_versions migration (0014) applied. 0014 has no foreign keys, so it stands alone
/// (the same approach as <c>JobDb</c>).
/// </summary>
public sealed class SoulDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
            return;
        await base.InitializeAsync();
        var files = new EmbeddedMigrationSource(typeof(SoulVersioningService).Assembly).Load(MigrationRunner.Common);
        var source = new InMemoryMigrationSource(files.Where(f => f.Version == 14));
        await new MigrationRunner(DataSource).MigrateAsync(source, MigrationRunner.Common);
    }
}
