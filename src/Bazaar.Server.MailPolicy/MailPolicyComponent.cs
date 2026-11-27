using Cdax.Core;
using Bazaar.Server.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.MailPolicy;

/// <summary>
/// The mail policy settings as a Bazaar Component (BAZAAR-COMPONENTS.md). Registers only the settings contribution: no I/O, no
/// mail runtime. The Mail module reads the keys; setup writes them (doc 45 step 11).
/// </summary>
public sealed class MailPolicyComponent : ICdaxModule
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "mail-policy",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:mail-policy"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddSingleton<ISettingContribution, MailPolicySettingContribution>();
    }
}
