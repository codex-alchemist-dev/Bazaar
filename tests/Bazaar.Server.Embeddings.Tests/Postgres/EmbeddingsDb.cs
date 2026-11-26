using Bazaar.Server.Data.Migrations;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Embeddings.Tests.Postgres;

/// <summary>Migrated throwaway database for the Embeddings SQL stores (embedding_profile from common/0008, vault tables from common/0009).</summary>
public sealed class EmbeddingsDb : PgFixture, IAsyncLifetime
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

    public static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
}
