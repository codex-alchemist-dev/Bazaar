using Bazaar.Core;
using Bazaar.Server.VmDensity.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.VmDensity.Tests;

public sealed class VmDensityComponentTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? settings = null, Action<IServiceCollection>? extra = null)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var services = new ServiceCollection();
        new VmDensityComponent().AddServices(services, configuration);
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Manifest_matches_the_decided_values()
    {
        ComponentManifest m = new VmDensityComponent().Manifest;
        Assert.Equal("vm-density", m.Id);
        Assert.Equal("0.1.0", m.Version);
        Assert.Equal(ComponentKind.Feature, m.Kind);
        Assert.Empty(m.DependsOn);
        Assert.Equal(["vms"], m.Soft);
        Assert.Equal(["vm-density:governor"], m.Provides);
    }

    [Fact]
    public void Registers_the_governor_and_binds_options()
    {
        using ServiceProvider provider = Build(new()
        {
            ["Bazaar:VmDensity:HostReserveMb"] = "2048",
            ["Bazaar:VmDensity:MaxRunning"] = "6",
            ["Bazaar:VmDensity:Enabled"] = "false",
        });

        Assert.IsType<DensityGovernor>(provider.GetRequiredService<IDensityGovernor>());
        VmDensityOptions o = provider.GetRequiredService<IOptions<VmDensityOptions>>().Value;
        Assert.Equal(2048, o.HostReserveMb);
        Assert.Equal(6, o.MaxRunning);
        Assert.False(o.Enabled);
    }

    [Fact]
    public void Options_defaults_match_doc_59()
    {
        var o = new VmDensityOptions();
        Assert.True(o.Enabled);
        Assert.Equal(4096, o.HostReserveMb);
        Assert.Equal(0, o.MaxRunning);
        Assert.Equal(TimeSpan.FromMinutes(10), o.QueueTimeout);
        Assert.Equal(64, o.MaxQueueLength);
        Assert.Equal(TimeSpan.FromSeconds(5), o.RecheckInterval);
        Assert.Equal(TimeSpan.FromMinutes(2), o.MinIdleBeforeSave);
        Assert.Equal(4, o.MaxSavesPerAdmission);
        Assert.False(o.AllowSmallerToPass);
        Assert.Equal("Bazaar:VmDensity", VmDensityOptions.SectionName);
    }

    [Fact]
    public async Task Without_host_ports_the_registered_governor_admits_everything()
    {
        using ServiceProvider provider = Build();
        var governor = Assert.IsType<DensityGovernor>(provider.GetRequiredService<IDensityGovernor>());
        Assert.False(governor.IsActive);
        await using AdmissionTicket ticket = await governor.AdmitAsync(new DensityVm(Guid.NewGuid(), "a", DensityGuestOs.Linux, 1_000_000), CancellationToken.None);
    }

    [Fact]
    public void With_host_ports_the_registered_governor_is_active()
    {
        var host = new FakeHost(TimeProvider.System, 16384);
        using ServiceProvider provider = Build(extra: s =>
        {
            s.AddSingleton<IHostMemoryProbe>(host);
            s.AddSingleton<IDensityVmDirectory>(host);
            s.AddSingleton<IDensityVmControl>(host);
        });

        Assert.True(Assert.IsType<DensityGovernor>(provider.GetRequiredService<IDensityGovernor>()).IsActive);
    }

    [Fact]
    public void A_host_supplied_time_provider_is_kept()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using ServiceProvider provider = Build(extra: s => s.AddSingleton<TimeProvider>(time));
        Assert.Same(time, provider.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Component_rejects_null_arguments()
    {
        var component = new VmDensityComponent();
        Assert.Throws<ArgumentNullException>(() => component.AddServices(null!, new ConfigurationBuilder().Build()));
        Assert.Throws<ArgumentNullException>(() => component.AddServices(new ServiceCollection(), null!));
    }

    [Fact]
    public void Admission_exception_carries_its_reason()
    {
        var ex = new DensityAdmissionException(DensityAdmissionFailure.QueueFull, "full");
        Assert.Equal(DensityAdmissionFailure.QueueFull, ex.Reason);
        Assert.Equal("full", ex.Message);
    }

    [Fact]
    public void Vm_activity_is_busy_for_any_active_signal()
    {
        DateTimeOffset t = DateTimeOffset.UnixEpoch;
        Assert.False(new VmActivity(false, false, false, t).IsBusy);
        Assert.True(new VmActivity(true, false, false, t).IsBusy);
        Assert.True(new VmActivity(false, true, false, t).IsBusy);
        Assert.True(new VmActivity(false, false, true, t).IsBusy);
        Assert.True(VmActivity.Unknown(t).IsBusy);
        Assert.True(VmActivity.Unknown(t).IsUnknown);
    }

    [Fact]
    public async Task The_default_activity_reader_reads_nothing_so_no_vm_can_be_reported_idle()
    {
        using ServiceProvider provider = Build();
        var vm = new DensityVm(Guid.NewGuid(), "agent", DensityGuestOs.Linux, 2048);

        var reader = provider.GetRequiredService<IVmActivityReader>();

        Assert.IsType<UnreadableVmActivityReader>(reader);
        Assert.Null(await reader.ReadAsync(vm, TestContext.Current.CancellationToken));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(vm, cts.Token));
    }

    [Fact]
    public void A_reader_the_host_registered_wins_over_the_default()
    {
        using ServiceProvider provider = Build(extra: s => s.AddSingleton<IVmActivityReader>(new OwnReader()));

        Assert.IsType<OwnReader>(provider.GetRequiredService<IVmActivityReader>());
    }

    private sealed class OwnReader : IVmActivityReader
    {
        public Task<VmActivity?> ReadAsync(DensityVm vm, CancellationToken cancellationToken) => Task.FromResult<VmActivity?>(null);
    }
}
