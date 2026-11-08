using Bazaar.Server.Data.Migrations;
using Bazaar.Server.Host.Composition;

namespace Bazaar.Server.Host.Startup;

/// <summary>Startup step 4: the database schema must match this build exactly. Migrations never run at start; only <c>migrate</c> applies them.</summary>
public sealed class MigrationSchemaGate(DatabaseConnection database) : ISchemaGate
{
    public async Task EnsureCurrentAsync(CancellationToken ct)
    {
        var dataSource = await database.GetAsync(ct);
        var source = new EmbeddedMigrationSource(typeof(MigrationRunner).Assembly);
        var components = HostComponents.All.Select(c => c.Manifest);
        await new MigrationRunner(dataSource, components: components).EnsureCurrentAsync(source, MigrateCommand.CurrentProvider(), ct);
    }
}
