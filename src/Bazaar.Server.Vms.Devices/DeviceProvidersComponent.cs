using Bazaar.Core;
using Bazaar.Server.Settings;
using Bazaar.Server.Setup;
using Bazaar.Server.Vms.Devices.Setup;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Vms.Devices;

/// <summary>The device providers' contribution to the settings registry (doc 62 section 7).</summary>
public sealed class DeviceProvidersSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => DeviceProvidersSettings.All;
}

/// <summary>
/// The device-guest listing as a Bazaar Component (doc 62 sections 7 and 9.1). Registers the <c>vms.devices.list</c> wire handler,
/// the <c>vms.devices.*</c>, <c>vms.android.*</c> and <c>vms.microvm.*</c> settings, and the "Mobile and Apple guests" setup step.
/// Registrations only: it starts no emulator or VM. The providers themselves implement <see cref="IDeviceProviderCatalogEntry"/>
/// and are registered by their own modules; this one only reads them.
/// </summary>
public sealed class DeviceProvidersComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vms-devices",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: ["settings"],
        Provides: ["feature:vms-devices"],
        SoftDependsOn: ["vms"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<ISettingContribution, DeviceProvidersSettingContribution>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupStep, DeviceProvidersSetupStep>());
        // The lifecycle router asks this gate whether an on-demand provider may take a new VM (doc 62 section 9.2).
        services.TryAddSingleton<IVmProviderGate>(sp => new DeviceProvidersGate(sp.GetRequiredService<ISettingStore>()));

        // Owner-only, read-only listing. The catalog entries are resolved when the handler is built, so a provider module
        // registered by the host is picked up; with none installed the reply is two empty lists.
        services.AddSingleton<IWireRequestHandler>(sp => new DeviceListWireHandler(
            sp.GetRequiredService<ISettingStore>(),
            sp.GetServices<IDeviceProviderCatalogEntry>()));
    }
}
