using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Components.Remote;

/// <summary>
/// The remote pairing and sign-in subsystem as a Bazaar Component (docs/design/32, group 32 WP1). Owns the <c>remote</c> migration
/// namespace (<c>Data/Sql/remote/</c>: humans, devices, refresh credentials and pairing offers).
/// </summary>
/// <remarks>
/// The token and pairing services are registered by the host composition (<c>AddBazaarRemoteAuth</c>), which also owns the
/// access-token issuer and the dev-only owner seed. Nothing is registered here yet, so the component stays the single owner of
/// the schema and its manifest, the same as <see cref="Bazaar.Server.Mail.MailComponent"/>.
/// </remarks>
public sealed class RemoteComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "remote",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:remote-auth"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
    }
}
