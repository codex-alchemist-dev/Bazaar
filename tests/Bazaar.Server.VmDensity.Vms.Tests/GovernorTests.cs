using Bazaar.Server.VmDensity.Abstractions;
using Bazaar.Server.Vms.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.VmDensity.Vms.Tests;

/// <summary>The real governor over the adapter: what it does under memory pressure with the default reader and with a reader that can see idleness.</summary>
public sealed class GovernorTests
{
    private sealed class Probe(FakeControl control) : IHostMemoryProbe
    {
        // 1000 MiB free before any save, 5000 after: the host reserve is 1000 and the request needs 2048.
        public Task<HostMemory> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HostMemory(16_000, control.Saved.Count == 0 ? 1000 : 5000));
    }

    private static ServiceProvider Build(FakeControl control, AgentVmInfo victim, FakeReader? reader)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:VmDensity:HostReserveMb"] = "1000",
            ["Bazaar:VmDensity:MinIdleBeforeSave"] = "00:00:00",
            ["Bazaar:VmDensity:QueueTimeout"] = "00:00:01",
            ["Bazaar:VmDensity:RecheckInterval"] = "00:00:00.100",
        }).Build();
        var services = new ServiceCollection();
        new VmDensityComponent().AddServices(services, config);
        new VmDensityVmsComponent().AddServices(services, config);
        services.AddSingleton<IAgentVmDirectory>(new FakeDirectory(victim));
        services.AddSingleton<IVmControl>(control);
        services.AddSingleton<IHostMemoryProbe>(new Probe(control));
        if (reader is not null)
            services.AddSingleton<IVmActivityReader>(reader);
        return services.BuildServiceProvider();
    }

    private static DensityVm Request() => new(Guid.CreateVersion7(), "someone-else", DensityGuestOs.Linux, 2048);

    [Fact]
    public async Task With_the_default_reader_the_governor_never_saves_and_the_start_times_out_in_the_queue()
    {
        var control = new FakeControl();
        using var sp = Build(control, Vm.Running(), reader: null);
        var governor = Assert.IsType<DensityGovernor>(sp.GetRequiredService<IDensityGovernor>());

        Assert.True(governor.IsActive); // the probe, directory and control are all supplied here
        var failure = await Assert.ThrowsAsync<DensityAdmissionException>(() => governor.AdmitAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(DensityAdmissionFailure.QueueTimeout, failure.Reason);
        Assert.Empty(control.Saved);
    }

    [Fact]
    public async Task A_reader_that_proves_idleness_lets_the_governor_save_that_vm()
    {
        var control = new FakeControl();
        var victim = Vm.Running();
        var reader = new FakeReader(_ => Task.FromResult<VmActivity?>(new VmActivity(false, false, false, DateTimeOffset.UtcNow - TimeSpan.FromHours(1))));
        using var sp = Build(control, victim, reader);
        var governor = sp.GetRequiredService<IDensityGovernor>();

        await using var ticket = await governor.AdmitAsync(Request(), TestContext.Current.CancellationToken);

        Assert.Equal([victim.VmId], control.Saved);
    }
}
