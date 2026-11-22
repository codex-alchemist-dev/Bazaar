using Bazaar.Core;
using Bazaar.Server.Org.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Org;

/// <summary>
/// The Company Org Chart module (docs/design/24 section 2). Registers the relation repository and the loan service, which
/// depend on the shared database and the GC components.
/// </summary>
public sealed class OrgComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "org",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["gc"],
        Provides: ["feature:company-org"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<OrgRelationRepository>();
        services.TryAddSingleton<OrgLoanService>();
    }
}
