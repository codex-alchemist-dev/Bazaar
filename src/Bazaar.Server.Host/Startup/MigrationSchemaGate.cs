using Bazaar.Server.Data.Migrations;

namespace Bazaar.Server.Host.Startup;

/// <summary>Startup step 4: the database schema must match this build exactly. Migrations never run at start; only <c>migrate</c> applies them.</summary>
public sealed class MigrationSchemaGate(DatabaseConnection database) : ISchemaGate
{
    public async Task EnsureCurrentAsync(CancellationToken ct)
    {
        var dataSource = await database.GetAsync(ct);
        var source = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly);
        await new MigrationRunner(dataSource).EnsureCurrentAsync(source, MigrateCommand.CurrentProvider(), ct);
    }
}
