using Bazaar.Core;
using Bazaar.Server.VmBench;
using Bazaar.Server.VmBench.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmBench.Tests;

public sealed class VmBenchComponentTests
{
    [Fact]
    public void Manifest_declares_id_version_kind_and_provides()
    {
        var m = new VmBenchComponent().Manifest;

        Assert.Equal("vm-bench", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Feature, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vm-bench:runner", "vm-bench:report"], m.Provides);
    }

    [Fact]
    public void AddServices_registers_runner_and_report_writer_as_singletons()
    {
        var services = new ServiceCollection();

        new VmBenchComponent().AddServices(services, new ConfigurationBuilder().Build());

        var runner = Assert.Single(services, d => d.ServiceType == typeof(IVmBenchRunner));
        Assert.Equal(typeof(VmBenchRunner), runner.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, runner.Lifetime);

        var writer = Assert.Single(services, d => d.ServiceType == typeof(IVmBenchReportWriter));
        Assert.Equal(typeof(VmBenchReportWriter), writer.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, writer.Lifetime);
    }
}
