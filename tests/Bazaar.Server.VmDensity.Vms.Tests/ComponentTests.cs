using Cdax.Core;
using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDensity.Vms.Tests;

public sealed class ComponentTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static ServiceProvider Build(IConfiguration configuration, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        new VmDensityComponent().AddServices(services, configuration);
        new VmDensityVmsComponent().AddServices(services, configuration);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_manifest_is_an_adapter_that_depends_on_the_density_module()
    {
        ComponentManifest m = new VmDensityVmsComponent().Manifest;

        Assert.Equal("vm-density-vms", m.Id);
        Assert.Equal(ComponentKind.Adapter, m.Kind);
        Assert.Equal(["vm-density"], m.DependsOn);
        Assert.Equal(["vms"], m.Soft);
    }

    [Fact]
    public async Task Without_the_vm_module_the_ports_exist_but_list_nothing_and_save_nothing()
    {
        using var sp = Build(Config());
        var directory = sp.GetRequiredService<IDensityVmDirectory>();
        var control = sp.GetRequiredService<IDensityVmControl>();

        Assert.Same(directory, control);
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.ListRunningAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => control.SaveAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task With_the_density_modules_default_reader_every_vm_is_busy_and_cannot_be_saved()
    {
        var info = Vm.Running();
        var control = new FakeControl();
        using var sp = Build(Config(), s =>
        {
            s.AddSingleton<IAgentVmDirectory>(new FakeDirectory(info));
            s.AddSingleton<IVmControl>(control);
        });

        var listed = Assert.Single(await sp.GetRequiredService<IDensityVmDirectory>().ListRunningAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sp.GetRequiredService<IDensityVmControl>().SaveAsync(info.VmId, TestContext.Current.CancellationToken));

        Assert.True(listed.Activity.IsBusy);
        Assert.True(listed.Activity.IsUnknown);
        Assert.Empty(control.Saved);
    }

    [Fact]
    public void A_registration_the_host_made_itself_wins()
    {
        using var sp = Build(Config(), s => s.AddSingleton<IDensityVmDirectory>(new OwnDirectory()));

        Assert.IsType<OwnDirectory>(sp.GetRequiredService<IDensityVmDirectory>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("61")]
    [InlineData("soon")]
    public void A_bad_timeout_fails_at_start(string value)
    {
        Assert.Throws<InvalidOperationException>(() => new VmDensityVmsComponent().AddServices(new ServiceCollection(), Config(("Bazaar:VmDensityVms:ReadTimeoutSeconds", value))));
    }

    private sealed class OwnDirectory : IDensityVmDirectory
    {
        public Task<IReadOnlyList<RunningVm>> ListRunningAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunningVm>>([]);
    }
}
