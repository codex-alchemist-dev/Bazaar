using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DevSandbox.Tests;

public class DevSandboxModuleTests
{
    [Fact]
    public async Task With_nothing_registered_every_run_is_refused()
    {
        using var sp = new ServiceCollection().AddBazaarDevSandbox().BuildServiceProvider();
        var runner = sp.GetRequiredService<ISandboxRunner>();
        var r = await runner.RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.Equal(SandboxVerdict.Refused, r.Verdict);
        Assert.IsType<SandboxCompanyFactory>(sp.GetRequiredService<ISandboxCompanyFactory>());
    }

    [Fact]
    public void TryAdd_keeps_existing_registrations()
    {
        var factory = new FakeCompanyFactory();
        using var sp = new ServiceCollection().AddSingleton<ISandboxCompanyFactory>(factory).AddBazaarDevSandbox().BuildServiceProvider();
        Assert.Same(factory, sp.GetRequiredService<ISandboxCompanyFactory>());
    }

    [Fact]
    public async Task Registered_provider_and_engine_are_picked_up()
    {
        var provider = new FakeProvider(OperatingSystem.IsWindows() ? SandboxOs.Windows : SandboxOs.Linux);
        var services = new ServiceCollection()
            .AddSingleton<ISandboxConfinementProvider>(provider)
            .AddSingleton<ISandboxEngine>(new ScriptedEngine([]))
            .AddBazaarDevSandbox();
        using var sp = services.BuildServiceProvider();
        var r = await sp.GetRequiredService<ISandboxRunner>().RunAsync(Specs.Valid(), TestContext.Current.CancellationToken);
        Assert.NotEqual(SandboxVerdict.Refused, r.Verdict);
        Assert.Equal(1, provider.Starts);
    }
}
