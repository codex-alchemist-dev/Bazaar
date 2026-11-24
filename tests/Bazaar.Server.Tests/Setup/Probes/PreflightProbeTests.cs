using Bazaar.Kodo.Probes.Host;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Probes;

namespace Bazaar.Server.Tests.Setup.Probes;

public sealed class PreflightProbeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, false, true, SetupProbeOutcome.Healthy)]
    [InlineData(false, true, true, SetupProbeOutcome.Healthy)]
    [InlineData(false, false, true, SetupProbeOutcome.Failed)]
    [InlineData(true, false, false, SetupProbeOutcome.Failed)]
    public async Task Os_is_supported_only_on_64_bit_windows_or_linux(bool windows, bool linux, bool is64, SetupProbeOutcome expected)
    {
        var facts = new FakeHostFacts { IsWindows = windows, IsLinux = linux, Is64Bit = is64 };

        var reading = await new HostOsProbe(facts).ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(expected, reading.Outcome);
    }

    [Fact]
    public async Task A_free_status_port_is_healthy_and_a_used_one_is_amber_not_red()
    {
        var facts = new FakeHostFacts();
        var plan = new SetupPlan { Network = new SetupNetworkSection { StatusPort = 8443 } };

        Assert.Equal(SetupProbeOutcome.Healthy, (await new HostPortsProbe(facts).ProbeAsync(ProbeTestPlans.Context(plan), Ct)).Outcome);

        facts.Listening.Add(8443);
        Assert.Equal(SetupProbeOutcome.Degraded, (await new HostPortsProbe(facts).ProbeAsync(ProbeTestPlans.Context(plan), Ct)).Outcome);
    }

    [Fact]
    public async Task A_status_port_outside_the_range_fails()
    {
        var plan = new SetupPlan { Network = new SetupNetworkSection { StatusPort = 70000 } };

        var reading = await new HostPortsProbe(new FakeHostFacts()).ProbeAsync(ProbeTestPlans.Context(plan), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
    }

    [Fact]
    public async Task The_data_root_defaults_to_the_platform_root_and_is_amber_until_it_exists()
    {
        var facts = new FakeHostFacts();

        var missing = await new HostDataRootProbe(facts).ProbeAsync(ProbeTestPlans.Context(), Ct);
        facts.ExistingDirectories.Add("/var/lib/bazaar");
        var present = await new HostDataRootProbe(facts).ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(SetupProbeOutcome.Degraded, missing.Outcome);
        Assert.Equal(SetupProbeOutcome.Healthy, present.Outcome);
    }

    [Fact]
    public async Task A_read_only_data_root_fails_and_the_message_never_repeats_the_path()
    {
        var facts = new FakeHostFacts();
        facts.ExistingDirectories.Add("/secret-place/data");
        facts.ReadOnlyDirectories.Add("/secret-place/data");
        var plan = new SetupPlan { Database = new SetupDatabaseSection { DataRoot = "/secret-place/data" } };

        var reading = await new HostDataRootProbe(facts).ProbeAsync(ProbeTestPlans.Context(plan), Ct);

        Assert.Equal(SetupProbeOutcome.Failed, reading.Outcome);
        Assert.DoesNotContain("secret-place", ProbeTestPlans.AllText(reading), StringComparison.Ordinal);
    }

    private sealed class Readings(long free, long total) : IHostReadings
    {
        public Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken) => Task.FromResult(new DiskReading(free, total));

        public Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(60, SetupProbeOutcome.Healthy)]
    [InlineData(10, SetupProbeOutcome.Degraded)]
    [InlineData(1, SetupProbeOutcome.Failed)]
    public async Task Disk_headroom_uses_the_kodo_thresholds(long freeOfHundred, SetupProbeOutcome expected)
    {
        var reading = await new HostDiskProbe(new FakeHostFacts(), new Readings(freeOfHundred, 100)).ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(expected, reading.Outcome);
    }

    [Fact]
    public async Task An_unknown_volume_or_a_failed_read_is_unavailable_never_green()
    {
        var noVolume = await new HostDiskProbe(new FakeHostFacts { Volume = null }, new Readings(60, 100)).ProbeAsync(ProbeTestPlans.Context(), Ct);
        var unreadable = await new HostDiskProbe(new FakeHostFacts(), new ThrowingReadings()).ProbeAsync(ProbeTestPlans.Context(), Ct);

        Assert.Equal(SetupProbeOutcome.Unavailable, noVolume.Outcome);
        Assert.Equal(SetupProbeOutcome.Unavailable, unreadable.Outcome);
    }

    private sealed class ThrowingReadings : IHostReadings
    {
        public Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken) => throw new IOException("denied");

        public Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void The_runtime_facts_answer_without_throwing_on_this_machine()
    {
        var facts = new RuntimeHostFacts();

        Assert.True(facts.IsWindows ^ facts.IsLinux || !(facts.IsWindows || facts.IsLinux));
        Assert.NotEmpty(facts.DefaultDataRoot);
        Assert.True(facts.DirectoryExists(AppContext.BaseDirectory));
        Assert.NotNull(facts.VolumeOf(AppContext.BaseDirectory));
        Assert.NotNull(facts.ListeningTcpPorts());
    }
}
