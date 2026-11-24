using Bazaar.Core;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDensity.Vms;

/// <summary>
/// Gives the density governor its VM directory and VM control (docs/design/59 section 8, work package 5; PROPOSED) as a Bazaar
/// Component. It registers <see cref="VmDensityVmAdapter"/> as <see cref="IDensityVmDirectory"/> and <see cref="IDensityVmControl"/>
/// over the VM module's <see cref="IAgentVmDirectory"/> and <see cref="IVmControl"/> (resolved when first needed; the control port is
/// absent when no VM backend is configured) and the <see cref="IVmActivityReader"/>. Registrations the host made itself win.
/// </summary>
/// <remarks>
/// Nothing in the host calls the governor yet, and the density module's default activity reader reads nothing, so every VM counts as
/// busy and none is ever listed as a save candidate. Loading this component therefore saves and stops nothing.
/// </remarks>
public sealed class VmDensityVmsComponent : IBazaarComponent
{
    /// <summary>The manifest, available without constructing the component.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-density-vms",
        Version: "0.1.0",
        Kind: ComponentKind.Adapter,
        DependsOn: ["vm-density"],
        Provides: ["vm-density:vm-ports"],
        SoftDependsOn: ["vms"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(VmDensityVmsOptions.Bind(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new VmDensityVmAdapter(
            sp.GetService<IAgentVmDirectory>(),
            sp.GetService<IVmControl>(),
            sp.GetService<IVmActivityReader>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<VmDensityVmsOptions>()));
        services.TryAddSingleton<IDensityVmDirectory>(sp => sp.GetRequiredService<VmDensityVmAdapter>());
        services.TryAddSingleton<IDensityVmControl>(sp => sp.GetRequiredService<VmDensityVmAdapter>());
    }
}
