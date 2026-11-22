using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Gateway.Catalogue;

/// <summary>
/// Gateway catalogue refresh as a Bazaar Component (docs/design/05-model-gateway.md, work package 11). Needs the gateway's
/// repositories (<c>AddBazaarGateway</c>), an <see cref="ICatalogueSchedule"/> and the shared <c>HttpClient</c> that <c>AddBazaarGateway</c> registers.
/// </summary>
public sealed class CatalogueComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "gateway-catalogue",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["settings"],
        Provides: ["gateway:catalogue"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddBazaarCatalogue();
    }
}

/// <summary>DI registration for the catalogue refresher and its scheduled service.</summary>
public static class CatalogueServiceCollectionExtensions
{
    public static IServiceCollection AddBazaarCatalogue(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ICatalogueStore>(sp => new GatewayCatalogueStore(sp.GetRequiredService<Gateway.Data.GatewayDirectoryRepository>()));
        services.TryAddSingleton<IProviderModelLister>(sp => new HttpProviderModelLister(
            sp.GetRequiredService<ICatalogueStore>(),
            sp.GetRequiredService<Pipeline.IProviderSecretSource>(),
            sp.GetRequiredService<HttpClient>()));
        services.TryAddSingleton<ICatalogueRefresher>(sp => new CatalogueRefresher(
            sp.GetRequiredService<ICatalogueStore>(),
            sp.GetRequiredService<IProviderModelLister>(),
            sp.GetService<TimeProvider>()));
        services.AddHostedService<CatalogueRefreshService>();
        return services;
    }
}
