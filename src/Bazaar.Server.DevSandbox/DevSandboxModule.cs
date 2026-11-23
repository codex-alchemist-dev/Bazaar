using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.DevSandbox;

/// <summary>Registers the developer sandbox core (doc 48 section 2.1).</summary>
public static class DevSandboxModule
{
    /// <summary>
    /// Adds the company factory and the runner. No confinement provider or engine is registered here: OS providers and engines plug
    /// in separately, and with none registered every run is refused.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddBazaarDevSandbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISandboxCompanyFactory, SandboxCompanyFactory>();
        services.TryAddSingleton<ISandboxRunner>(sp => new SandboxRunner(
            sp.GetServices<ISandboxConfinementProvider>(),
            sp.GetServices<ISandboxEngine>(),
            sp.GetRequiredService<ISandboxCompanyFactory>()));
        return services;
    }
}
