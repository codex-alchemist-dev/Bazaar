using Bazaar.Kodo.Probes.Host;
using Bazaar.Server.Setup;
using Bazaar.Server.Setup.Host;

namespace Bazaar.Server.Tests.Setup.Verification;

/// <summary>The probe-backed and Kodo-backed checks that replace the not-evaluated placeholders (doc 45 section 1.12).</summary>
public sealed class SetupProbeBackedChecksTests
{
    private static SetupVerificationContext Context() => new(SetupPureChecksTests.PlanWith());

    private sealed class FakeProbe(string id, SetupProbeReading? reading = null, Exception? error = null) : ISetupInfrastructureProbe
    {
        public string CheckId { get; } = id;

        public Task<SetupProbeReading> ProbeAsync(SetupVerificationContext context, CancellationToken cancellationToken) =>
            error is not null ? throw error : Task.FromResult(reading!);
    }

    private sealed class FakeReadings : IHostReadings
    {
        public DiskReading Disk { get; set; } = new(FreeBytes: 60, TotalBytes: 100);

        public TimeSpan Offset { get; set; } = TimeSpan.Zero;

        public bool ClockThrows { get; set; }

        public Task<DiskReading> ReadDiskAsync(string volume, CancellationToken cancellationToken) =>
            volume == "bad" ? throw new IOException("no such volume") : Task.FromResult(volume == "full" ? new DiskReading(1, 100) : Disk);

        public Task<MemoryReading> ReadMemoryAsync(CancellationToken cancellationToken) => Task.FromResult(new MemoryReading(50, 100));

        public Task<TimeSpan> ReadClockOffsetAsync(CancellationToken cancellationToken) =>
            ClockThrows ? throw new NotSupportedException() : Task.FromResult(Offset);
    }

    [Theory]
    [InlineData(SetupProbeOutcome.Healthy, SetupCheckStatus.Green)]
    [InlineData(SetupProbeOutcome.Degraded, SetupCheckStatus.Amber)]
    [InlineData(SetupProbeOutcome.Failed, SetupCheckStatus.Red)]
    [InlineData(SetupProbeOutcome.Unavailable, SetupCheckStatus.NotEvaluated)]
    public async Task A_probe_outcome_maps_to_a_status_and_unavailable_is_never_green(SetupProbeOutcome outcome, SetupCheckStatus expected)
    {
        var check = new ProbeBackedCheck("database.postgres", "PostgreSQL", new FakeProbe("database.postgres", new SetupProbeReading(outcome, "reading")));

        var result = await check.RunAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task A_check_with_no_probe_is_not_evaluated()
    {
        var result = await new ProbeBackedCheck("instance.lock", "Lock", null).RunAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStatus.NotEvaluated, result.Status);
    }

    [Fact]
    public async Task A_throwing_probe_becomes_a_red_row_for_that_check_only()
    {
        var checks = new ISetupVerificationCheck[]
        {
            new ProbeBackedCheck("a", "A", new FakeProbe("a", error: new InvalidOperationException("boom"))),
            new ProbeBackedCheck("b", "B", new FakeProbe("b", new SetupProbeReading(SetupProbeOutcome.Healthy, "fine"))),
        };

        var report = await new SetupVerifier(checks).VerifyAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStatus.Red, report.Checks[0].Status);
        Assert.Equal(SetupCheckStatus.Green, report.Checks[1].Status);
    }

    [Fact]
    public async Task Disk_check_is_green_amber_or_red_by_kodo_thresholds()
    {
        var readings = new FakeReadings();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(SetupCheckStatus.Green, (await new KodoDiskHeadroomCheck(readings, ["ok"]).RunAsync(Context(), ct)).Status);

        readings.Disk = new DiskReading(FreeBytes: 15, TotalBytes: 100);
        Assert.Equal(SetupCheckStatus.Amber, (await new KodoDiskHeadroomCheck(readings, ["ok"]).RunAsync(Context(), ct)).Status);

        Assert.Equal(SetupCheckStatus.Red, (await new KodoDiskHeadroomCheck(readings, ["full"]).RunAsync(Context(), ct)).Status);
    }

    [Fact]
    public async Task Disk_check_reports_the_worst_volume_and_an_unreadable_one_is_not_green()
    {
        var readings = new FakeReadings();
        var ct = TestContext.Current.CancellationToken;

        var worst = await new KodoDiskHeadroomCheck(readings, ["ok", "full"]).RunAsync(Context(), ct);
        var unreadable = await new KodoDiskHeadroomCheck(readings, ["ok", "bad"]).RunAsync(Context(), ct);

        Assert.Equal(SetupCheckStatus.Red, worst.Status);
        Assert.Equal(SetupCheckStatus.NotEvaluated, unreadable.Status);
    }

    [Fact]
    public void Disk_check_needs_a_volume()
    {
        Assert.Throws<ArgumentException>(() => new KodoDiskHeadroomCheck(new FakeReadings(), []));
    }

    [Theory]
    [InlineData(0, SetupCheckStatus.Green)]
    [InlineData(-1000, SetupCheckStatus.Amber)]
    [InlineData(10000, SetupCheckStatus.Red)]
    public async Task Clock_check_follows_the_drift_thresholds(int milliseconds, SetupCheckStatus expected)
    {
        var readings = new FakeReadings { Offset = TimeSpan.FromMilliseconds(milliseconds) };

        var result = await new KodoClockSyncCheck(readings).RunAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task An_unreadable_clock_is_not_evaluated_and_never_green()
    {
        var result = await new KodoClockSyncCheck(new FakeReadings { ClockThrows = true }).RunAsync(Context(), TestContext.Current.CancellationToken);

        Assert.Equal(SetupCheckStatus.NotEvaluated, result.Status);
    }

    [Fact]
    public async Task The_runtime_readings_give_a_real_disk_result_and_an_honest_clock_result()
    {
        var readings = new DriveInfoHostReadings();
        var volume = Path.GetPathRoot(AppContext.BaseDirectory)!;
        var ct = TestContext.Current.CancellationToken;

        var disk = await new KodoDiskHeadroomCheck(readings, [volume]).RunAsync(Context(), ct);
        var clock = await new KodoClockSyncCheck(readings).RunAsync(Context(), ct);

        Assert.NotEqual(SetupCheckStatus.NotEvaluated, disk.Status);
        Assert.Equal(SetupCheckStatus.NotEvaluated, clock.Status);
    }

    [Fact]
    public void Create_keeps_the_order_and_ids_and_swaps_in_real_checks()
    {
        var probe = new FakeProbe("database.postgres", new SetupProbeReading(SetupProbeOutcome.Healthy, "ok"));

        var checks = SetupVerificationChecks.Create([probe], new FakeReadings(), ["ok"]);

        Assert.Equal(SetupVerificationChecks.All.Select(check => check.Id), checks.Select(check => check.Id));
        Assert.IsType<KodoDiskHeadroomCheck>(checks.Single(check => check.Id == "host.disk"));
        Assert.IsType<KodoClockSyncCheck>(checks.Single(check => check.Id == "host.clock"));
        Assert.IsType<ProbeBackedCheck>(checks.Single(check => check.Id == "database.postgres"));
        Assert.IsType<NotEvaluatedCheck>(checks.Single(check => check.Id == "instance.lock"));
    }

    [Fact]
    public void Create_without_inputs_is_the_placeholder_list_and_refuses_duplicate_probes()
    {
        Assert.All(SetupVerificationChecks.Create().Skip(SetupVerificationChecks.Pure.Count), check => Assert.IsType<NotEvaluatedCheck>(check));

        var reading = new SetupProbeReading(SetupProbeOutcome.Healthy, "ok");
        Assert.Throws<ArgumentException>(() => SetupVerificationChecks.Create([new FakeProbe("x", reading), new FakeProbe("x", reading)]));
    }

    [Fact]
    public async Task A_full_report_with_real_probes_can_complete_green_only_when_every_check_is_answered()
    {
        var readings = new FakeReadings();
        var healthy = new SetupProbeReading(SetupProbeOutcome.Healthy, "ok");
        var probes = SetupVerificationChecks.Infrastructure
            .Where(check => check.Id is not ("host.disk" or "host.clock"))
            .Select(check => (ISetupInfrastructureProbe)new FakeProbe(check.Id, healthy))
            .ToList();
        var plan = SetupPlanLoader.Load(SetupFixtures.Valid, RegistrySetupSettingCatalog.Known).Plan!;
        var ct = TestContext.Current.CancellationToken;

        var full = await new SetupVerifier(SetupVerificationChecks.Create(probes, readings, ["ok"])).VerifyAsync(new SetupVerificationContext(plan), ct);
        var noClock = await new SetupVerifier(SetupVerificationChecks.Create(probes, new FakeReadings { ClockThrows = true }, ["ok"])).VerifyAsync(new SetupVerificationContext(plan), ct);

        Assert.Equal(SetupCheckStatus.Green, full.Overall);
        Assert.Equal(100, full.Score);
        Assert.Equal(SetupCheckStatus.Amber, noClock.Overall);
    }
}
