using Bazaar.Core;
using Bazaar.Server.Terminal.Abstractions;
using Bazaar.Server.VmTransport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Terminal.Vms;

/// <summary>
/// Gives the terminal module its VM backend (docs/design/55, section 3, "Seam") as a Bazaar Component: it registers
/// <see cref="VmGuestPtyChannel"/> as <see cref="IGuestPtyChannel"/> over the guest transport's <see cref="IGuestChannelDirectory"/>
/// (resolved when first needed; a missing directory leaves the channel unavailable and the broker fails closed).
/// </summary>
/// <remarks>
/// The terminal module registers its own fail-closed channel with <c>TryAdd</c>, so this component depends on <c>terminal</c> to start after
/// it and then replaces that registration. It is not part of the host composition yet: the host does not compose the terminal module.
/// </remarks>
public sealed class TerminalVmsComponent : IBazaarComponent
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "terminal-vms",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["terminal"],
        Provides: ["terminal:vm-channel"],
        SoftDependsOn: ["vms"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Replace, not TryAdd: the terminal module's "no backend" default is already registered by the time this runs.
        services.RemoveAll<IGuestPtyChannel>();
        services.AddSingleton<IGuestPtyChannel>(sp => new VmGuestPtyChannel(sp.GetService<IGuestChannelDirectory>()));
    }
}
