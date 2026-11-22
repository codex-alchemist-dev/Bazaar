using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DevSandbox.Windows;

/// <summary>Registers the Windows confinement provider (doc 48 section 2.1, Windows provider).</summary>
public static class WindowsSandboxModule
{
    /// <summary>Registers the operator-configured launch plan (see <see cref="ConfiguredWindowsLaunchPlan"/>). Call it before <see cref="AddBazaarDevSandboxWindows"/>.</summary>
    public static IServiceCollection AddBazaarDevSandboxWindowsConfiguredPlan(this IServiceCollection services, ConfiguredWindowsLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        return services.AddSingleton<IWindowsSandboxLaunchPlan>(new ConfiguredWindowsLaunchPlan(options));
    }

    /// <summary>
    /// Adds the provider on a Windows host where an <see cref="IWindowsSandboxLaunchPlan"/> is already registered (call this after the plan); on any other host, or without a plan, nothing
    /// is added, so every run is refused. The restricted-token fallback never counts as tested.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="options">Work root, system directories and limits; null uses the defaults.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarDevSandboxWindows(this IServiceCollection services, WindowsConfinementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!OperatingSystem.IsWindows() || !services.Any(d => d.ServiceType == typeof(IWindowsSandboxLaunchPlan)))
        {
            return services;
        }

        services.AddSingleton<ISandboxConfinementProvider>(sp =>
            new WindowsConfinementProvider(options ?? new WindowsConfinementOptions(), sp.GetRequiredService<IWindowsSandboxLaunchPlan>()));
        return services;
    }
}
