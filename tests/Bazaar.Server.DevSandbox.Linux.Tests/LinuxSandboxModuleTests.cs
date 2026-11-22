using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DevSandbox.Linux.Tests;

public sealed class LinuxSandboxModuleTests
{
    private sealed class Plan : ILinuxSandboxLaunchPlan
    {
        public LinuxLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => new("/bin/true", []);
    }

    [Fact]
    public void Without_a_launch_plan_nothing_is_registered()
    {
        var services = new ServiceCollection().AddBazaarDevSandboxLinux();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISandboxConfinementProvider));
    }

    [Fact]
    public void With_a_launch_plan_the_provider_is_registered_on_linux_only()
    {
        var services = new ServiceCollection().AddSingleton<ILinuxSandboxLaunchPlan, Plan>().AddBazaarDevSandboxLinux();

        Assert.Equal(OperatingSystem.IsLinux(), services.Any(d => d.ServiceType == typeof(ISandboxConfinementProvider)));
    }

    [Fact]
    public void The_configured_plan_extension_registers_a_plan_the_provider_extension_then_accepts()
    {
        var services = new ServiceCollection()
            .AddBazaarDevSandboxLinuxConfiguredPlan(new ConfiguredLinuxLaunchOptions { ModuleRoot = "/m", Executable = "/usr/bin/runner" })
            .AddBazaarDevSandboxLinux();

        Assert.Contains(services, d => d.ServiceType == typeof(ILinuxSandboxLaunchPlan));
        Assert.Equal(OperatingSystem.IsLinux(), services.Any(d => d.ServiceType == typeof(ISandboxConfinementProvider)));
    }
}
