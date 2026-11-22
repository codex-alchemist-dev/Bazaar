using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Vault.Data;

/// <summary>
/// Shared Postgres fixture for the Vault tests, referenced by tests/Bazaar.Server.Vault.Tests and tests/Bazaar.Server.Tests
/// (Constellation/VaultGraphSourceTests), so keep it self-contained.
/// </summary>
public sealed class VaultDb : PgFixture, IAsyncLifetime
{
    public override async ValueTask InitializeAsync()
    {
        if (PgSource.IsUnsupportedHost)
        {
            return;
        }

        await base.InitializeAsync();
        var source = new EmbeddedMigrationSource(typeof(AssemblyMarker).Assembly);
        await new MigrationRunner(DataSource).MigrateAsync(source, "linux");
    }
}
