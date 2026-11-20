using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.WebAdmin;

/// <summary>Admin-only web panel gate (docs/design/51-web-admin-module.md). Work package 2: authorization only; no routes or assets yet.</summary>
public sealed class WebAdminComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "web-admin",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["roles", "audit"],
        Provides: ["feature:web-admin"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAdminSessionReader, NoSessionReader>();
        services.TryAddSingleton<IAdminGate, AdminGate>();
    }
}
