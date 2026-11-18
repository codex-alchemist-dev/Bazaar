using Bazaar.Server.AgentSafety.Containment;
using Bazaar.Vms.Environments;
using Bazaar.Vms.Lifecycle;
using Bazaar.Vms.WindowsSandbox;
using Bazaar.Vms.WindowsSandbox.WsbCli;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// The Vms module in the host: the environment repository and store, the operation locks, and the lifecycle service. The lifecycle
/// service needs a backend, so it is registered only when one is configured. Without a backend, the agent pauser stays fail closed.
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

        var backend = WindowsSandboxFromConfiguration(configuration);
        if (backend is not null)
        {
            services.TryAddSingleton<IVmBackend>(backend);
            services.TryAddSingleton(sp => new VmLifecycleService(
                sp.GetRequiredService<IEnvironmentVmStore>(),
                sp.GetRequiredService<IVmBackend>(),
                sp.GetRequiredService<VmOperationLocks>(),
                sp.GetRequiredService<TimeProvider>()));
        }

        return services;
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
