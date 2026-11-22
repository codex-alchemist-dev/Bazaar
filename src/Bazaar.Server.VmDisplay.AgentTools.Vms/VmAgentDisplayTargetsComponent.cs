using Bazaar.Core;
using Bazaar.Server.Vms.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDisplay.AgentTools.Vms;

/// <summary>
/// Gives the screen tools their agent display targets (docs/design/59, section 4) as a Bazaar Component. It is off by default:
/// unless <c>Bazaar:VmDisplayAgentTargets:Enabled</c> is true it registers nothing, so the tools component finds no
/// <see cref="IAgentDisplayTargets"/> and offers no screen tool to any agent. When enabled it registers
/// <see cref="VmAgentDisplayTargets"/> over <see cref="IAgentVmDirectory"/>, resolved when first needed (a missing directory gives no target).
/// </summary>
/// <remarks>
/// The tools component decides what to offer while services are being registered, so this component has to register first. It has no hard
/// dependency on the tools component for that reason (a hard edge would order it after); the tools manifest lists this id as a soft
/// dependency, and the kernel starts components with no hard edge between them in id order, which puts this id first. The host's
/// composition test fails if that order changes.
/// </remarks>
public sealed class VmAgentDisplayTargetsComponent : IBazaarComponent
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display-agent-targets",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: [],
        Provides: ["vm-display:agent-targets"],
        SoftDependsOn: ["vms"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Bind first so a bad setting fails at start even while the adapter is off.
        AgentTargetsOptions options = AgentTargetsOptions.Bind(configuration);
        if (!options.Enabled)
            return;

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        // TryAdd: a registration the host made itself wins.
        services.TryAddSingleton<IAgentDisplayTargets>(sp => new VmAgentDisplayTargets(
            sp.GetService<IAgentVmDirectory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AgentTargetsOptions>()));
    }
}
