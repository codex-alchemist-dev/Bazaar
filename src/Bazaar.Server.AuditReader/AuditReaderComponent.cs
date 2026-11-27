using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.AuditReader;

/// <summary>Read-only audit port for the web admin panel (docs/design/51-web-admin-module.md section 5). No SQL migrations of its own.</summary>
public sealed class AuditReaderComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "audit-reader",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["audit"],
        Provides: ["feature:audit-reader"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarAuditReader();
}
