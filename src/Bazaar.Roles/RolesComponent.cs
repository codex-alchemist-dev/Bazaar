using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Roles;

/// <summary>Roles with floors (docs/design/48-gap-decisions-security-identity.md section 4). SQL lives in the <c>roles</c> migration namespace.</summary>
public sealed class RolesComponent : ICdaxModule
{
    public const string MigrationProvider = "roles";

    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "roles",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["audit"],
        Provides: ["feature:roles"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarRoles();
}
