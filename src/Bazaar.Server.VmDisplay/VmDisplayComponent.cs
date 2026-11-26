using Bazaar.Core;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.VmDisplay;

/// <summary>
/// Screenshot and input for agent VMs as a Bazaar Component (docs/design/59, section 4). The module holds no hypervisor, guest
/// or browser code: the host registers adapters for <see cref="IHypervisorConsole"/>, <see cref="IDisplayGuestExec"/> and
/// <see cref="IBrowserPageDriver"/>, and the real <see cref="IDisplayGate"/> and <see cref="IDisplayAuditSink"/>. Until the host
/// supplies a gate the module refuses everything.
/// </summary>
public sealed class VmDisplayComponent : IBazaarComponent
{
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "vm-display",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["vm-display:capture", "vm-display:input"],
        SoftDependsOn: ["vms", "browser", "security", "audit"]);

    public ComponentManifest Manifest => ManifestDefinition;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(VmDisplayOptions.Bind(configuration));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDisplayGate, UnconfiguredDisplayGate>();
        services.TryAddSingleton<IDisplayAuditSink, NoopDisplayAuditSink>();
        services.TryAddSingleton<IGuestDisplay, GuestDisplayService>();
    }
}
