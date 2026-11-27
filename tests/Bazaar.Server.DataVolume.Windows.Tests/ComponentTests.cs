using Cdax.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DataVolume.Windows.Tests;

public sealed class ComponentTests
{
    [Fact]
    public void The_manifest_names_the_module_and_depends_on_the_data_volume_module()
    {
        var manifest = new WindowsDataVolumeComponent().Manifest;

        Assert.Equal("data-volume-windows", manifest.Id);
        Assert.Equal(ComponentKind.Provider, manifest.Kind);
        Assert.Equal(["data-volume"], manifest.DependsOn);
    }

    [Fact]
    public void Services_register_only_on_windows()
    {
        var services = new ServiceCollection();

        new WindowsDataVolumeComponent().AddServices(services, new ConfigurationBuilder().Build());

        var kinds = services.Where(d => d.ServiceType == typeof(IDataVolumeProvider) || d.ServiceType == typeof(IDataVolumeOperator)).ToList();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(2, kinds.Count);
            using var provider = services.BuildServiceProvider();
            Assert.Equal("bitlocker-vhdx", provider.GetRequiredService<IDataVolumeProvider>().Kind);
            Assert.Equal("bitlocker-vhdx", provider.GetRequiredService<IDataVolumeOperator>().Kind);
        }
        else
        {
            Assert.Empty(services);
        }
    }
}
