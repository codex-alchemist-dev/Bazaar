using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Vms.Density;

/// <summary>Registers the lifecycle adapters for the density governor (doc 62 section 9.3).</summary>
public static class VmsDensityRegistration
{
    /// <summary>
    /// Registers <see cref="IDensityVmDirectory"/> and <see cref="IDensityVmControl"/> over <see cref="VmLifecycleService"/>, but only
    /// when an <see cref="IDensityGovernor"/> is registered (the <c>vm-density</c> component). The host calls it after the components
    /// have registered. Without a governor it adds nothing, so nothing lists or saves VMs on the governor's behalf.
    /// </summary>
    /// <returns>True when the adapters were registered.</returns>
    public static bool AddVmDensityLifecycleAdapters(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(d => d.ServiceType == typeof(IDensityGovernor)))
            return false;

        services.TryAddSingleton<IDensityVmDirectory>(sp => new LifecycleDensityVmDirectory(sp.GetRequiredService<VmLifecycleService>));
        services.TryAddSingleton<IDensityVmControl>(sp => new LifecycleDensityVmControl(sp.GetRequiredService<VmLifecycleService>));
        return true;
    }
}
