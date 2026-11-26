using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Gateway.SystemPolicy;

/// <summary>
/// Gateway system-agent policy seeding as a Bazaar Component (docs/design/05-model-gateway.md, "System-agent policy seeding").
/// Needs the gateway's repositories (<c>AddBazaarGateway</c>) and the settings store.
/// </summary>
public sealed class SystemPolicyComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "gateway-system-policy",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["settings"],
        Provides: ["gateway:system-policy"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBazaarSystemPolicy();
    }
}

/// <summary>DI registration for the seeder. Other modules add their own <see cref="ISystemAgentPolicySource"/>.</summary>
public static class SystemPolicyServiceCollectionExtensions
{
    public static IServiceCollection AddBazaarSystemPolicy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISystemPolicyStore, RepositorySystemPolicyStore>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISystemAgentPolicySource, EmbeddingsPolicySource>());
        services.TryAddSingleton<SystemAgentPolicySeeder>();
        services.AddHostedService(sp => sp.GetRequiredService<SystemAgentPolicySeeder>());
        return services;
    }
}
