using Cdax.Core;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.StatusProviders;

/// <summary>Queue depths and health checks for the admin status page and the status TUI (docs/design/51-web-admin-module.md section 5). No migrations of its own.</summary>
public sealed class StatusProvidersComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "status-providers",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:status-providers"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarStatusProviders();
}

public static class StatusProvidersRegistration
{
    public static IServiceCollection AddBazaarStatusProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IStatusFactsSource>(sp => new PostgresStatusFactsSource(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IAdminQueueDepthSource>(sp => new AgentQueueDepthSource(sp.GetRequiredService<IStatusFactsSource>()));
        services.AddSingleton<IAdminHealthSource>(sp => new CoreHealthSource(sp.GetRequiredService<IStatusFactsSource>()));
        return services;
    }
}
