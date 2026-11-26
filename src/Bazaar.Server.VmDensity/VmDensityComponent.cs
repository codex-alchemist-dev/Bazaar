using Bazaar.Core;
using Bazaar.Server.VmDensity.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDensity;

/// <summary>
/// The VM density governor as a Bazaar Component (docs/design/59 section 8). Registers the options (<c>Bazaar:VmDensity</c>) and the
/// <see cref="IDensityGovernor"/>. The host adapts <see cref="IHostMemoryProbe"/>, <see cref="IDensityVmDirectory"/> and
/// <see cref="IDensityVmControl"/> onto the platform and the VM lifecycle service. Density control is an optimisation, not a security
/// control: with any of those three missing the governor admits every start.
/// </summary>
public sealed class VmDensityComponent : IBazaarComponent
{
    /// <summary>The component manifest.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-density",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["vm-density:governor"],
        SoftDependsOn: ["vms"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<VmDensityOptions>().Bind(configuration.GetSection(VmDensityOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDensityGovernor, DensityGovernor>();
    }
}
