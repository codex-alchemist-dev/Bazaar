using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Server.Providers;
using Bazaar.Server.Vms.Environments;
using Bazaar.Server.Vms.Lifecycle;
using Bazaar.Server.Vms.WindowsSandbox;
using Bazaar.Server.Vms.WindowsSandbox.WsbCli;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The Vms module in the host: the environment repository and store, the operation locks, the backend router and the lifecycle
/// service (doc 62 section 9.2). Provider modules contribute their own <see cref="IVmBackendSource"/>; the host lists none by name
/// except the Windows Sandbox backend, which is built here from its configuration section. With no source registered, every lifecycle
/// call is refused with <see cref="VmRefusalReason.NoProvider"/> and the agent pauser stays fail closed.
/// </summary>
public static class VmsCompositionExtensions
{
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

        var sandbox = WindowsSandboxFromConfiguration(configuration);
        if (sandbox is not null)
        {
            services.AddSingleton<IVmBackendSource>(sandbox);
        }

        // Sources are resolved when the router is built, so a provider component registered after this call is still routed to.
        services.TryAddSingleton<IVmBackendRouter>(sp => new VmBackendRouter(
            sp.GetServices<IVmBackendSource>(),
            PairSelection(sp.GetService<ProviderCapabilityReport>()),
            sp.GetService<IVmProviderGate>()));
        services.TryAddSingleton(sp => new VmLifecycleService(
            sp.GetRequiredService<IEnvironmentVmStore>(),
            sp.GetRequiredService<IVmBackendRouter>(),
            sp.GetRequiredService<VmOperationLocks>(),
            sp.GetRequiredService<TimeProvider>()));

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
