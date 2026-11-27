using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MemberRoster;

/// <summary>Read-only members list (agents and humans) for the web admin panel (docs/design/51-web-admin-module.md section 5). No SQL migrations of its own.</summary>
public sealed class MemberRosterComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "member-roster",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["remote"],
        Provides: ["feature:member-roster"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration) => services.AddBazaarMemberRoster();
}
