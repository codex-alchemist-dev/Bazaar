using Cdax.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>
/// The Hyper-V adapter for the vm-display module as a Bazaar Component (docs/design/59, sections 3 and 4). It registers one
/// <see cref="IHypervisorConsole"/> next to any others, so several backends can coexist. Live behaviour is UNVERIFIED (spike S-DSP).
/// </summary>
public sealed class HyperVComponent : ICdaxModule
{
    /// <summary>The manifest, available without an instance.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display-hyperv",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["vm-display"],
        Provides: ["vm-display:hypervisor-hyperv"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(HyperVOptions.Bind(configuration));
        services.TryAddSingleton<IHyperVScriptRunner>(sp => new ProcessHyperVScriptRunner(sp.GetRequiredService<HyperVOptions>()));
        services.AddSingleton<IHypervisorConsole>(sp => new HyperVConsole(
            sp.GetRequiredService<HyperVOptions>(),
            sp.GetRequiredService<IHyperVScriptRunner>()));
    }
}
