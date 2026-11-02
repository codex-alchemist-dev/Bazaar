using Bazaar.Constellation.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Constellation.Vulkan;

public static class BazaarConstellationVulkanServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Vulkan Constellation backend. Only host composition calls this.
    /// TODO(component): move to the Bazaar component registration once the component model merges.
    /// </summary>
    public static IServiceCollection AddBazaarConstellationVulkan(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IConstellationRenderer, VulkanConstellationRenderer>();
        return services;
    }
}
