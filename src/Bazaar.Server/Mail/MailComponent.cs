using Bazaar.Server.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Mail;

/// <summary>
/// The external mail subsystem as a Bazaar Component (docs/design/22-email-subsystem.md). Owns the <c>mail</c> migration namespace.
/// Only external mail uses it: internal agent coordination stays in GCs.
/// </summary>
/// <remarks>
/// No mail services are registered yet: the back ends, the inbound and outbound pipelines and the quarantine store are later
/// work packages (doc 22 WP3 to WP12). Add registrations here as they land, so the component stays the single owner of its services.
/// </remarks>
public sealed class MailComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "mail",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:mail"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
    }
}
