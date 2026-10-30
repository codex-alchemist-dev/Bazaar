using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Components;

/// <summary>
/// The entry point of a Bazaar Component. The kernel calls <see cref="AddServices"/> in dependency order, and only for enabled
/// components (docs/design/BAZAAR-COMPONENTS.md section 3). A component registers its own services and nothing else.
/// </summary>
public interface IBazaarComponent
{
    ComponentManifest Manifest { get; }

    void AddServices(IServiceCollection services, IConfiguration configuration);
}
