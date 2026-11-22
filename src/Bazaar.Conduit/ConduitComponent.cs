using Bazaar.Conduit.Settings;
using Bazaar.Core;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Conduit;

/// <summary>
/// Conduit (docs/design/39-conduit-remote-connectivity.md) as a Bazaar Component. It contributes the Conduit settings keys
/// (<see cref="ConduitSettingContribution"/>), so the setup plan, the wizard and the admin panel know them. Registrations only: no
/// I/O, and no module starts here. Every exposing module stays off until the owner approves it.
/// </summary>
public sealed class ConduitComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "conduit",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:conduit"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<ISettingContribution, ConduitSettingContribution>();
    }
}
