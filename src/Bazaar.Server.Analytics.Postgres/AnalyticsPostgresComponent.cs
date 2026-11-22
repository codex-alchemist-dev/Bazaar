using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Analytics.Postgres;

/// <summary>
/// Registers the Kanban and revert collectors for company analytics. Needs the shared <see cref="NpgsqlDataSource"/> the host
/// registers. <c>Bazaar:Analytics:RevertAuthorRole</c> (default <c>author</c>) names the outcome attribution role that counts as the
/// author of a reverted change.
/// </summary>
public sealed class AnalyticsPostgresComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "analytics-postgres",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["analytics"],
        Provides: ["analytics:collector:kanban", "analytics:collector:revert"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        var role = configuration["Bazaar:Analytics:RevertAuthorRole"];
        services.AddSingleton<IWorkEventCollector>(sp => new KanbanWorkEventCollector(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IWorkEventCollector>(sp => new RevertWorkEventCollector(
            sp.GetRequiredService<NpgsqlDataSource>(), string.IsNullOrWhiteSpace(role) ? "author" : role.Trim()));
    }
}
