using Cdax.Core;
using Bazaar.Server.Security.Credentials;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDisplay.Gate;

/// <summary>
/// Joins the display module to the Security read contracts as a Bazaar Component (docs/design/59, section 5). It replaces the
/// display module's refusing default with <see cref="SecurityDisplayGate"/>, whichever component is added first. It registers no
/// implementation of the Security readers: when one is missing from the container the gate refuses everything.
/// </summary>
public sealed class VmDisplayGateComponent : ICdaxModule
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display-gate",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["vm-display"],
        Provides: ["vm-display:gate"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        DisplayGateOptions options = DisplayGateOptions.Bind(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        // Replace, not TryAdd: the display module registers its refusing gate with TryAdd, so a replace works in either order, and a
        // later host registration still wins. The readers are optional at resolve time; a missing one makes the gate refuse.
        services.Replace(ServiceDescriptor.Singleton<IDisplayGate>(sp => new SecurityDisplayGate(
            sp.GetRequiredService<DisplayGateOptions>(),
            sp.GetService<IKillSwitchReader>(),
            sp.GetService<IAgentSessionTaintReader>(),
            sp.GetService<ILeasedCredentialMatcher>(),
            sp.GetService<IBrowserSessionClassifier>(),
            sp.GetService<IDisplayInputApproval>(),
            sp.GetRequiredService<TimeProvider>())));
    }
}
