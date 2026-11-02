using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Vault;

/// <summary>
/// The Vault as a Bazaar Component (docs/design/18-vault.md, docs/design/BAZAAR-COMPONENTS.md). Markdown files with wiki-links in a
/// git repo are the source of truth; Postgres only indexes them. The component owns the Vault data, git and link code.
/// </summary>
/// <remarks>
/// No Vault service is registered in DI today: the Vault repositories, <c>IGitProvider</c> and the link resolver have no
/// registration in the host, so this component registers nothing yet. Add a registration here when the Vault is wired into
/// the host, so the component stays the single owner of its services.
/// </remarks>
public sealed class VaultComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vault",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:vault"]);

    public ComponentManifest Manifest => ManifestDefinition;

    // TODO(component): the vault migrations (common/0009_vault.sql and later vault tables) move to migration namespace "vault"
    // once agent/wp-migration-namespaces merges. Until then the SQL stays in common/.
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
    }
}
