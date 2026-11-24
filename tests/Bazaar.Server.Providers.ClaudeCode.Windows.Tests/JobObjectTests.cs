using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Providers.ClaudeCode.Windows.Tests;

public sealed class JobObjectTests
{
    [Fact]
    public void The_component_registers_the_container_only_on_windows()
    {
        var services = new ServiceCollection();
        new ClaudeCodeWindowsComponent().AddServices(services, new ConfigurationBuilder().Build());
        using var sp = services.BuildServiceProvider();
        Assert.Equal(OperatingSystem.IsWindows(), sp.GetService<IChildProcessContainer>() is not null);
    }

    [Fact]
    public void Closing_the_job_ends_an_adopted_child()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job Objects exist only on Windows.");
        if (OperatingSystem.IsWindows())
        {
            RunOnWindows();
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunOnWindows()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var job = new WindowsJobObject();
        job.Adopt(child);
        Assert.False(child.HasExited);
        job.Dispose();
        Assert.True(child.WaitForExit(10_000), "the child must end when the job handle closes");
    }
}
