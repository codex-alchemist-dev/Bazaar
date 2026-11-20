using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DevSandbox.Linux;

/// <summary>Registers the Linux confinement provider (doc 48 section 2.1, Linux provider).</summary>
public static class LinuxSandboxModule
{
    /// <summary>
    /// Adds the provider on a Linux host where an <see cref="ILinuxSandboxLaunchPlan"/> is already registered (call this after the plan). On any other host, or without a plan,
    /// nothing is added, so every run is refused (fail closed, no unconfined fallback). The provider's startup self-test decides whether it
    /// counts as tested.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Tool locations and limits; null uses the defaults of a standard util-linux host.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarDevSandboxLinux(this IServiceCollection services, LinuxConfinementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!OperatingSystem.IsLinux() || !services.Any(d => d.ServiceType == typeof(ILinuxSandboxLaunchPlan)))
        {
            return services;
        }

        // The OS check above guards the construction; the analyzer cannot see through the factory delegate.
#pragma warning disable CA1416
        services.AddSingleton<ISandboxConfinementProvider>(sp =>
            new LinuxConfinementProvider(options ?? new LinuxConfinementOptions(), sp.GetRequiredService<ILinuxSandboxLaunchPlan>()));
#pragma warning restore CA1416
        return services;
    }
}
