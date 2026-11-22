using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Kodo;

public static class KodoRegistration
{
    /// <summary>Registers the snapshot holder (as provider and publisher) and the empty incident source.</summary>
    public static IServiceCollection AddBazaarKodo(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<KodoSnapshotHolder>();
        services.TryAddSingleton<IKodoSnapshotProvider>(sp => sp.GetRequiredService<KodoSnapshotHolder>());
        services.TryAddSingleton<IKodoSnapshotPublisher>(sp => sp.GetRequiredService<KodoSnapshotHolder>());
        services.TryAddSingleton<IKodoIncidentSource, NoKodoIncidents>();
        return services;
    }
}

/// <summary>Server-side holder of the live Kodo snapshot (docs/design/46-kodo-health-module.md section 15.7). No SQL migrations.</summary>
public sealed class KodoComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "kodo",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:kodo"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarKodo();
}
