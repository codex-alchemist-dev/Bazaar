using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Providers;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.HyperV;
using Bazaar.Server.Vms.HyperV.PowerShell;
using Bazaar.Server.Vms.Kvm;
using Bazaar.Server.Vms.Kvm.Virsh;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.Ports;
using Bazaar.Server.Vms.WindowsSandbox;
using Bazaar.Server.Vms.WindowsSandbox.WsbCli;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using HyperVParentResolver = Bazaar.Server.Vms.HyperV.DirectoryParentResolver;
using KvmParentResolver = Bazaar.Server.Vms.Kvm.DirectoryParentResolver;
using ModuleAgentVmDirectory = Bazaar.Server.Vms.Abstractions.IAgentVmDirectory;
using ModuleVmControl = Bazaar.Server.Vms.Abstractions.IVmControl;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The Vms module in the host: the environment repository and store, the operation locks, the backend router and the lifecycle
/// service (doc 62 section 9.2). Provider modules contribute their own <see cref="IVmBackendSource"/>; the host lists none by name
/// except the Windows Sandbox backend, which is built here from its configuration section. With no source registered, every lifecycle
/// call is refused with <see cref="VmRefusalReason.NoProvider"/> and the agent pauser stays fail closed.
/// The Vms module in the host: the environment repository and store, the operation locks, and the lifecycle service. The lifecycle
/// service needs a backend, so it is registered only when one is configured. Without a backend, the agent pauser stays fail closed.
/// The cross-module ports (<c>Bazaar.Server.Vms.Abstractions</c>) follow the same rule: the directory only reads records and is always
/// registered; the control port needs the lifecycle service, so it exists only with a backend.
/// </summary>
public static class VmsCompositionExtensions
{
    /// <summary>Configuration section for the Hyper-V backend. Keys: ImageDirectory (golden image VHDX files), VmDirectory (child disks), ScriptDirectory.</summary>
    public const string HyperVSection = "Bazaar:Vms:HyperV";

    /// <summary>Configuration section for the KVM backend. Keys: ImageDirectory (golden image qcow2 files), VmDirectory (child disks), ConnectionUri (default qemu:///system).</summary>
    public const string KvmSection = "Bazaar:Vms:Kvm";

    /// <summary>Configuration section for the Windows Sandbox backend. Keys: HostBaseImage (GUID), ConfigDirectory, Networking.</summary>
    public const string WindowsSandboxSection = "Bazaar:Vms:WindowsSandbox";

    /// <summary>Registers the Vms module. Expects the shared <c>NpgsqlDataSource</c> to be registered already.</summary>
    public static IServiceCollection AddBazaarVms(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new EnvironmentRepository(sp.GetRequiredService<NpgsqlDataSource>()));
        services.TryAddSingleton<IEnvironmentVmStore>(sp => new EnvironmentVmStore(sp.GetRequiredService<EnvironmentRepository>()));
        services.TryAddSingleton<IAgentVmDirectory>(sp => new RepositoryAgentVmDirectory(sp.GetRequiredService<EnvironmentRepository>()));
        services.TryAddSingleton(new VmOperationLocks());
        // The read port for other modules (not the agent pauser's directory above): it reads records and calls no backend.
        services.TryAddSingleton<ModuleAgentVmDirectory>(sp => new EnvironmentAgentVmDirectory(sp.GetRequiredService<EnvironmentRepository>(), sp.GetService<IVmBackend>()));

        var sandbox = WindowsSandboxFromConfiguration(configuration);
        if (sandbox is not null)
        {
            services.AddSingleton<IVmBackendSource>(sandbox);
        // Hyper-V is the VM provider for the agent pair and is chosen first; Windows Sandbox serves one-off work and is the fallback (doc 50).
        IVmBackend? backend = HyperVFromConfiguration(configuration);
        // Hyper-V is the VM provider for the agent pair and is chosen first, then KVM on Linux hosts; Windows Sandbox serves one-off work and is the fallback (doc 50).
        // Each backend's host facts fail closed on the wrong OS, so the host decides in practice.
        IVmBackend? backend = HyperVFromConfiguration(configuration);
        backend ??= KvmFromConfiguration(configuration);
        backend ??= WindowsSandboxFromConfiguration(configuration);
        if (backend is not null)
        {
            services.TryAddSingleton(backend);
            services.TryAddSingleton(sp => new VmLifecycleService(
                sp.GetRequiredService<IEnvironmentVmStore>(),
                sp.GetRequiredService<IVmBackend>(),
                sp.GetRequiredService<VmOperationLocks>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IAgentContainmentCheck>()));
                sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<ModuleVmControl>(sp => new LifecycleVmControl(sp.GetRequiredService<VmLifecycleService>()));
        }

        // Sources are resolved when the router is built, so a provider component registered after this call is still routed to.
        services.TryAddSingleton<IVmBackendRouter>(sp => new VmBackendRouter(
            sp.GetServices<IVmBackendSource>(),
            PairSelection(sp.GetService<ProviderCapabilityReport>()),
            sp.GetService<IVmProviderGate>()));
        // The governor, the device-guest policy and the activity source are optional (doc 62 section 9.3): each is resolved when the
        // lifecycle is built, so a component that registers one is used, and a host without it still starts VMs.
        services.TryAddSingleton(sp => new VmLifecycleService(
            sp.GetRequiredService<IEnvironmentVmStore>(),
            sp.GetRequiredService<IVmBackendRouter>(),
            sp.GetRequiredService<VmOperationLocks>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IDensityGovernor>(),
            sp.GetService<IDeviceGuestPolicy>(),
            sp.GetService<IVmActivitySource>()));

        return services;
    }

    /// <summary>The VM role's selected provider kind from the startup report, or none.</summary>
    public static VmPairSelection PairSelection(ProviderCapabilityReport? report) =>
        new(report?.Selections.FirstOrDefault(s => s.Role == ProviderRole.Vm)?.Provider?.Kind);

    /// <summary>
    /// The pair candidates for the provider selector (doc 62 section 9.2): every registered <see cref="VmProviderPlacement.Pair"/> source
    /// that is also an <see cref="IVmProvider"/>, in <see cref="VmBackendOrdering"/> order. On-demand providers are never pair candidates.
    /// </summary>
    public static IReadOnlyList<IVmProvider> PairCandidates(IEnumerable<IVmBackendSource> sources) =>
        VmBackendOrdering.Order(sources).Where(s => s.Placement == VmProviderPlacement.Pair).OfType<IVmProvider>().ToList();
    /// <summary>The Hyper-V backend, or null when its section does not name all three directories.</summary>
    private static HyperVBackend? HyperVFromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(HyperVSection);
        var imageDirectory = section["ImageDirectory"];
        var vmDirectory = section["VmDirectory"];
        var scriptDirectory = section["ScriptDirectory"];
        if (string.IsNullOrWhiteSpace(imageDirectory) || string.IsNullOrWhiteSpace(vmDirectory) || string.IsNullOrWhiteSpace(scriptDirectory))
        {
            return null;
        }

        return new HyperVBackend(
            new PowerShellHyperVHost(new ProcessHyperVCommandRunner(), scriptDirectory, vmDirectory),
            new DirectoryParentResolver(imageDirectory),
            new HyperVHostFacts());
    }

            new HyperVParentResolver(imageDirectory),
            new HyperVHostFacts());
    }

    /// <summary>The KVM backend, or null when its section does not name an image directory and a VM directory.</summary>
    private static KvmBackend? KvmFromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(KvmSection);
        var imageDirectory = section["ImageDirectory"];
        var vmDirectory = section["VmDirectory"];
        if (string.IsNullOrWhiteSpace(imageDirectory) || string.IsNullOrWhiteSpace(vmDirectory))
        {
            return null;
        }

        var connectionUri = section["ConnectionUri"];
        var options = string.IsNullOrWhiteSpace(connectionUri)
            ? new VirshKvmOptions(vmDirectory)
            : new VirshKvmOptions(vmDirectory, connectionUri);
        return new KvmBackend(
            new VirshKvmHost(new ProcessKvmCommandRunner(), options),
            new KvmParentResolver(imageDirectory),
            new KvmHostFacts());
    }

    /// <summary>The Windows Sandbox backend, or null when its section does not name a base image and a config directory.</summary>
    private static WindowsSandboxBackend? WindowsSandboxFromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(WindowsSandboxSection);
        var configDirectory = section["ConfigDirectory"];
        if (!Guid.TryParse(section["HostBaseImage"], out var baseImage) || string.IsNullOrWhiteSpace(configDirectory))
        {
            return null;
        }

        var networking = Enum.TryParse<WindowsSandboxNetworking>(section["Networking"], ignoreCase: true, out var parsed)
            ? parsed
            : WindowsSandboxNetworking.Disable;
        return new WindowsSandboxBackend(
            new WsbCliSandboxHost(new ProcessWsbCommandRunner(), configDirectory),
            new WindowsSandboxHostFacts(),
            new WindowsSandboxOptions(baseImage, networking));
    }
}
