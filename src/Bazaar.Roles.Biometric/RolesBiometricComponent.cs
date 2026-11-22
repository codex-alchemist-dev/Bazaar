using Bazaar.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Roles.Biometric;

/// <summary>The biometric layer of the owner-signed grant widening gate (docs/design/48-gap-decisions-security-identity.md section 8.1).</summary>
public sealed class RolesBiometricComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "roles-biometric",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["roles"],
        Provides: ["feature:roles-biometric"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarRolesBiometric();
}
