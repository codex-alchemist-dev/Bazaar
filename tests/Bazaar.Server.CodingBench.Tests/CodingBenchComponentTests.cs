using Bazaar.Core;
using Bazaar.Server.CodingBench.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.CodingBench.Tests;

public sealed class CodingBenchComponentTests
{
    [Fact]
    public void Manifest_matches_the_module_contract()
    {
        var manifest = new CodingBenchComponent().Manifest;

        Assert.Equal("coding-bench", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal(ComponentKind.Feature, manifest.Kind);
        Assert.Empty(manifest.DependsOn);
        Assert.Equal(["coding-bench:runner", "coding-bench:report"], manifest.Provides);
    }

    [Fact]
    public void Component_is_a_valid_kernel_module()
    {
        var ordered = ComponentGraph.Order([new CodingBenchComponent().Manifest]);

        Assert.Single(ordered);
    }

    [Fact]
    public void AddServices_registers_runner_and_report_writer_as_singletons()
    {
        var services = new ServiceCollection();

        new CodingBenchComponent().AddServices(services, new ConfigurationBuilder().Build());

        var runner = Assert.Single(services, d => d.ServiceType == typeof(ICodingBenchRunner));
        Assert.Equal(typeof(CodingBenchRunner), runner.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, runner.Lifetime);

        var writer = Assert.Single(services, d => d.ServiceType == typeof(ICodingBenchReportWriter));
        Assert.Equal(typeof(CodingBenchReportWriter), writer.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, writer.Lifetime);
    }

    [Fact]
    public async Task Approval_defaults_to_deny_until_the_host_supplies_one()
    {
        var services = new ServiceCollection();
        new CodingBenchComponent().AddServices(services, new ConfigurationBuilder().Build());

        await using var provider = services.BuildServiceProvider();
        var approval = provider.GetRequiredService<ICodingBenchApproval>();

        Assert.False(await approval.IsApprovedAsync(new CodingBenchOptions(["model-a"]), CancellationToken.None));
    }

    [Fact]
    public async Task A_host_approval_registered_first_is_kept()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodingBenchApproval>(new FixedCodingBenchApproval(true));
        new CodingBenchComponent().AddServices(services, new ConfigurationBuilder().Build());

        await using var provider = services.BuildServiceProvider();
        var approval = provider.GetRequiredService<ICodingBenchApproval>();

        Assert.True(await approval.IsApprovedAsync(new CodingBenchOptions(["model-a"]), CancellationToken.None));
    }
}
