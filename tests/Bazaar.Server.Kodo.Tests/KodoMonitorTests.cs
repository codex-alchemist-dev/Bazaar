using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Kodo.Tests;

public class KodoMonitorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task First_pass_with_green_readings_is_healthy_with_the_default_components()
    {
        var time = Make.Clock();
        using var monitor = Make.Monitor(new FakeReadings(), time);

        Assert.Null(monitor.Latest);
        await monitor.RunCycleAsync(Ct);

        var snapshot = monitor.Latest!;
        Assert.Equal("healthy", snapshot.Status.State);
        Assert.Equal(100, snapshot.Status.Score);
        Assert.Equal(
            ["host.disk.free.data", "host.memory.pressure", "kodo.self.journal_writable", "kodo.self.spill"],
            snapshot.Components.Select(c => c.Id).ToArray());
        Assert.All(snapshot.Components, c => Assert.Equal("green", c.State));
        Assert.NotNull(snapshot.JournalHead);
        Assert.DoesNotContain("host.clock.drift", monitor.ProbeIds);
    }

    [Fact]
    public async Task Only_probes_that_are_due_run_again()
    {
        var time = Make.Clock();
        var readings = new FakeReadings();
        using var monitor = Make.Monitor(readings, time);

        await monitor.RunCycleAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(30));
        await monitor.RunCycleAsync(Ct);
        Assert.Equal(1, readings.DiskReads);

        time.Advance(TimeSpan.FromSeconds(30));
        await monitor.RunCycleAsync(Ct);
        Assert.Equal(2, readings.DiskReads);
        Assert.Equal(2, readings.MemoryReads);
    }

    [Fact]
    public async Task A_critical_red_probe_fails_the_state_caps_the_score_and_publishes_the_change()
    {
        var time = Make.Clock();
        var readings = new FakeReadings();
        var publisher = new RecordingPublisher();
        using var monitor = Make.Monitor(readings, time, publisher);
        await monitor.RunCycleAsync(Ct);

        readings.DiskUsedPercent = 97;
        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);

        var snapshot = monitor.Latest!;
        Assert.Equal("failing", snapshot.Status.State);
        Assert.InRange(snapshot.Status.Score, 0, 49);
        Assert.Contains(snapshot.Components, c => c.Id == "host.disk.free.data" && c.State == "red" && c.Cause == "disk_usage_high");
        var change = Assert.Single(publisher.Events, e => e.Type == KodoStreamEventDto.StateChangedType);
        Assert.Equal("healthy", change.StateChange!.Previous);
        Assert.Equal("failing", change.StateChange.State);
        Assert.Contains(monitor.RecentJournal(20), r => r.Kind == "state" && r.Summary == "state healthy -> failing");
    }

    [Fact]
    public async Task Failing_recovers_through_recovering_after_the_stability_window()
    {
        var time = Make.Clock();
        var readings = new FakeReadings { DiskUsedPercent = 97 };
        using var monitor = Make.Monitor(readings, time);
        await monitor.RunCycleAsync(Ct);
        Assert.Equal("failing", monitor.Latest!.Status.State);

        readings.DiskUsedPercent = 40;
        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);
        Assert.Equal("recovering", monitor.Latest!.Status.State);

        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);
        Assert.Equal("recovering", monitor.Latest!.Status.State);

        time.Advance(TimeSpan.FromMinutes(5));
        await monitor.RunCycleAsync(Ct);
        Assert.Equal("healthy", monitor.Latest!.Status.State);
    }

    [Fact]
    public async Task A_non_critical_amber_counts_only_after_its_grace()
    {
        var time = Make.Clock();
        var readings = new FakeReadings { MemoryUsedPercent = 92 };
        using var monitor = Make.Monitor(readings, time);
        await monitor.RunCycleAsync(Ct);
        Assert.Equal("healthy", monitor.Latest!.Status.State);

        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);
        Assert.Equal("healthy", monitor.Latest!.Status.State);

        time.Advance(TimeSpan.FromMinutes(1));
        await monitor.RunCycleAsync(Ct);
        Assert.Equal("degraded", monitor.Latest!.Status.State);
    }

    [Fact]
    public async Task A_throwing_reading_becomes_unknown_never_green_and_never_escapes()
    {
        var time = Make.Clock();
        using var monitor = Make.Monitor(new FakeReadings { Throw = true }, time);

        await monitor.RunCycleAsync(Ct);

        var disk = monitor.Latest!.Components.Single(c => c.Id == "host.disk.free.data");
        Assert.Equal("unknown", disk.State);
        Assert.Equal("unreadable", disk.Cause);
        Assert.Equal("degraded", monitor.Latest.Status.State);
        Assert.InRange(monitor.Latest.Status.Score, 0, 70);
        Assert.DoesNotContain(@"C:\secret", System.Text.Json.JsonSerializer.Serialize(monitor.RecentJournal(50)));
    }

    [Fact]
    public async Task A_failing_publisher_does_not_stop_the_pass()
    {
        var time = Make.Clock();
        using var monitor = Make.Monitor(new FakeReadings(), time, new RecordingPublisher { Throw = true });

        await monitor.RunCycleAsync(Ct);

        Assert.Equal("healthy", monitor.Latest!.Status.State);
    }

    [Fact]
    public async Task Heartbeats_follow_their_interval_and_revisions_are_gap_free()
    {
        var time = Make.Clock();
        var publisher = new RecordingPublisher();
        using var monitor = Make.Monitor(new FakeReadings(), time, publisher);

        await monitor.RunCycleAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(5));
        await monitor.RunCycleAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(5));
        await monitor.RunCycleAsync(Ct);

        Assert.Equal([1L, 2L], publisher.Events.Select(e => e.Revision).ToArray());
        Assert.All(publisher.Events, e => Assert.Equal(KodoStreamEventDto.HeartbeatType, e.Type));
        Assert.Equal(2, monitor.Latest!.Revision);
    }

    [Fact]
    public async Task Cancellation_is_the_only_thing_a_pass_throws()
    {
        var time = Make.Clock();
        using var monitor = Make.Monitor(new FakeReadings(), time);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.RunCycleAsync(cts.Token));
    }

    [Fact]
    public async Task A_journal_that_cannot_open_its_file_is_reported_red()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "kodo-blocker-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blocker, "not a directory", Ct);
        try
        {
            using var monitor = Make.Monitor(new FakeReadings(), Make.Clock(), options: new KodoServerOptions { DataDirectory = blocker });

            await monitor.RunCycleAsync(Ct);

            Assert.Contains(monitor.Latest!.Components, c => c.Id == "kodo.self.journal_writable" && c.State == "red" && c.Cause == "journal_not_writable");
            Assert.Equal("failing", monitor.Latest.Status.State);
            Assert.Null(monitor.Latest.JournalHead);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task The_spill_journal_survives_a_restart_and_keeps_its_chain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kodo-spill-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new KodoServerOptions { DataDirectory = dir };
            string firstHead;
            using (var first = Make.Monitor(new FakeReadings(), Make.Clock(), options: options))
            {
                await first.RunCycleAsync(Ct);
                firstHead = first.Latest!.JournalHead!;
            }

            using var second = Make.Monitor(new FakeReadings(), Make.Clock(), options: options);
            await second.RunCycleAsync(Ct);

            var entries = second.RecentJournal(100);
            Assert.Contains(entries, e => e.Hash == firstHead);
            Assert.Contains(entries, e => e.Kind == "start" && e.Sequence > 0);
            Assert.All(entries.OrderBy(e => e.Sequence).Skip(1).Zip(entries.OrderBy(e => e.Sequence)), pair => Assert.Equal(pair.Second.Hash, pair.First.PreviousHash));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Invalid_or_loosening_operator_settings_fall_back_and_are_listed()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Kodo:Settings:kodo.state.stability_window_s"] = "10",
                ["Bazaar:Kodo:Settings:kodo.probe.timeout_ms.default"] = "1500",
                ["Bazaar:Kodo:TickSeconds"] = "2",
            })
            .Build();

        var options = KodoServiceCollectionExtensions.ReadOptions(config.GetSection("Bazaar:Kodo"));

        Assert.Equal(300, options.Settings.GetInt(KodoSettingKey.StabilityWindowSeconds));
        Assert.Equal(1500, options.Settings.GetInt(KodoSettingKey.ProbeTimeoutMs));
        Assert.Single(options.SettingErrors);
        Assert.StartsWith("kodo.state.stability_window_s", options.SettingErrors[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), options.TickInterval);
    }
}
