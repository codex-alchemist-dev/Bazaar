using Bazaar.Constellation.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Constellation.Vulkan.Tests;

public class BazaarConstellationVulkanServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_the_vulkan_renderer_behind_the_thin_interface()
    {
        var services = new ServiceCollection();
        services.AddBazaarConstellationVulkan();
        using ServiceProvider provider = services.BuildServiceProvider();

        IConstellationRenderer renderer = provider.GetRequiredService<IConstellationRenderer>();
        Assert.IsType<VulkanConstellationRenderer>(renderer);
    }

    [Fact]
    public void Rejects_a_null_collection() =>
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddBazaarConstellationVulkan());
}
