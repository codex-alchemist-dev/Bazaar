using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.DevSandbox.Windows.Tests;

public sealed class WindowsSandboxModuleTests
{
    private sealed class Plan : IWindowsSandboxLaunchPlan
    {
        public WindowsLaunchCommand Create(SandboxRunSpec spec, string workDirectory) => new("cmd.exe", []);
    }

    [Fact]
    public void Without_a_launch_plan_nothing_is_registered()
    {
        var services = new ServiceCollection().AddBazaarDevSandboxWindows();

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISandboxConfinementProvider));
    }

    [Fact]
    public void With_a_launch_plan_the_provider_is_registered_on_windows_only()
    {
        var services = new ServiceCollection().AddSingleton<IWindowsSandboxLaunchPlan, Plan>().AddBazaarDevSandboxWindows();

        Assert.Equal(OperatingSystem.IsWindows(), services.Any(d => d.ServiceType == typeof(ISandboxConfinementProvider)));
    }
}
