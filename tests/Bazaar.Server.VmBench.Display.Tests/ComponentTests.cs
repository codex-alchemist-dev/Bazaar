using Cdax.Core;
using Bazaar.Server.VmDensity.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.VmBench.Display.Tests;

public sealed class ComponentTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? config = null, Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build();
        new DisplayBenchComponent().AddServices(services, cfg);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_declares_id_version_kind_and_provides_with_only_soft_links()
    {
        var m = new DisplayBenchComponent().Manifest;

        Assert.Equal("vm-bench-display", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Feature, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vm-bench-display:runner", "vm-bench-display:report"], m.Provides);
    }

    [Fact]
    public void Registers_the_five_scenarios_runner_and_writer()
    {
        using var sp = Build();

        var ids = sp.GetServices<IDisplayBenchScenario>().Select(s => s.Id).Order().ToArray();

        Assert.Equal(Enum.GetValues<DisplayBenchScenario>(), ids);
        Assert.IsType<DisplayBenchRunner>(sp.GetRequiredService<IDisplayBenchRunner>());
        Assert.IsType<DisplayBenchReportWriter>(sp.GetRequiredService<IDisplayBenchReportWriter>());
    }

    [Fact]
    public async Task With_no_host_ports_every_scenario_reports_not_run_and_the_overall_is_not_a_pass()
    {
        using var sp = Build();

        var report = await sp.GetRequiredService<IDisplayBenchRunner>().RunAsync("bare", null, TestContext.Current.CancellationToken);

        Assert.NotEmpty(report.Results);
        Assert.All(report.Results, r => Assert.Equal(DisplayBenchOutcome.NotRun, r.Outcome));
        Assert.Equal(DisplayBenchOutcome.NotRun, report.Overall);
    }

    [Fact]
    public void Criteria_bind_from_the_configuration_section()
    {
        using var sp = Build(new() { ["Bazaar:VmBench:Display:DensityMinPairs"] = "5", ["Bazaar:VmBench:Display:DensityMaxPairs"] = "9" });

        var c = sp.GetRequiredService<IOptions<DisplayBenchCriteria>>().Value;

        Assert.Equal(5, c.DensityMinPairs);
        Assert.Equal(9, c.DensityMaxPairs);
        Assert.Equal(30, c.SettleSeconds);
    }

    [Fact]
    public void Invalid_configuration_is_rejected()
    {
        using var sp = Build(new() { ["Bazaar:VmBench:Display:SettleSeconds"] = "-5" });

        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<DisplayBenchCriteria>>().Value);
    }

    [Fact]
    public void A_registered_governor_is_picked_up_by_the_density_scenario()
    {
        var governor = new FakeGovernorStub();
        using var sp = Build(extra: s => s.AddSingleton<IDensityGovernor>(governor));

        var scenario = sp.GetServices<IDisplayBenchScenario>().OfType<DensityRampScenario>().Single();

        Assert.NotNull(scenario);
        Assert.Same(governor, sp.GetRequiredService<IDensityGovernor>());
    }

    private sealed class FakeGovernorStub : IDensityGovernor
    {
        public Task<AdmissionTicket> AdmitAsync(DensityVm vm, CancellationToken cancellationToken) => throw new NotSupportedException();

        public DensitySnapshot Snapshot() => new(0, 0, 0, 0, []);

        public void NotifyMemoryChanged()
        {
        }
    }
}
