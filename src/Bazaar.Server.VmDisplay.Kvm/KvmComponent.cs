using Cdax.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>
/// The KVM adapter for the vm-display module as a Bazaar Component (docs/design/59, sections 3 and 4). It registers one
/// <see cref="IHypervisorConsole"/> next to any others, so several backends can coexist.
/// </summary>
public sealed class KvmComponent : ICdaxModule
{
    /// <summary>The manifest, available without an instance.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display-kvm",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["vm-display"],
        Provides: ["vm-display:hypervisor-kvm"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        KvmOptions options = KvmOptions.Bind(configuration);
        services.AddSingleton(options);
        services.TryAddSingleton<IQmpTransport>(sp => new VirshQmpTransport(sp.GetRequiredService<KvmOptions>()));
        services.TryAddSingleton<IScreendumpFileReader>(sp => new LocalScreendumpFileReader(sp.GetRequiredService<KvmOptions>().MaxScreendumpBytes));
        services.AddSingleton<IHypervisorConsole>(sp => new KvmConsole(
            sp.GetRequiredService<KvmOptions>(),
            sp.GetRequiredService<IQmpTransport>(),
            sp.GetRequiredService<IScreendumpFileReader>()));
    }
}
