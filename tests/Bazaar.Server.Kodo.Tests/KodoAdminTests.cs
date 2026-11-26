using Bazaar.Contracts.Kodo;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Kodo.Tests;

public sealed class KodoAdminTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-kodo-admin-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static KodoStatusSnapshotDto Snapshot(string state, int score, DateTimeOffset asOf, string? under = null, params KodoComponentDto[] components) =>
        new(KodoStatusSnapshotDto.CurrentSchemaVersion, 7, new KodoStatusDto(state, under, score, asOf, []), "ab", components);

    [Fact]
    public void Components_map_to_the_words_the_dashboard_grades_and_the_overall_check_comes_first()
    {
        var now = Make.Start;
        var snapshot = Snapshot("failing", 31, now, null,
            new KodoComponentDto("a.green", "green", null, now),
            new KodoComponentDto("b.amber", "amber", "slow", now),
            new KodoComponentDto("c.red", "red", "disk_usage_high", now),
            new KodoComponentDto("d.unknown", "unknown", "no_reading", now));

        var view = KodoAdminViews.FromSnapshot(snapshot, now, KodoAdminViews.DefaultStaleAfter);

        Assert.Equal(31, view.Score);
        Assert.Equal(
            [("overall", "failing", null), ("a.green", "healthy", null), ("b.amber", "degraded", "slow"), ("c.red", "failing", "disk_usage_high"), ("d.unknown", "unknown", "no_reading")],
            view.Checks.Select(c => (c.Name, c.State, c.Detail)).ToArray());
        Assert.Empty(view.Incidents);
    }

    [Fact]
    public void Frozen_state_derives_one_critical_incident_and_names_the_underlying_state()
    {
        var now = Make.Start;
        var view = KodoAdminViews.FromSnapshot(Snapshot("frozen", 20, now, "failing"), now, KodoAdminViews.DefaultStaleAfter);

        var incident = Assert.Single(view.Incidents);
        Assert.Equal(KodoAdminViews.IncidentModeId, incident.Id);
        Assert.Equal("critical", incident.Severity);
        Assert.Contains("underlying failing", incident.Title, StringComparison.Ordinal);
        Assert.Equal("underlying failing", view.Checks[0].Detail);
    }

    [Fact]
    public void A_snapshot_older_than_the_silence_budget_has_no_score_and_reads_unobserved_not_green()
    {
        var now = Make.Start;
        var snapshot = Snapshot("healthy", 100, now.AddMinutes(-5), null, new KodoComponentDto("x", "green", null, now));

        var view = KodoAdminViews.FromSnapshot(snapshot, now, KodoAdminViews.DefaultStaleAfter);

        Assert.Null(view.Score);
        Assert.Equal("unobserved", view.Checks[0].State);
        Assert.Empty(view.Incidents);
    }

    [Fact]
    public async Task The_adapter_reports_disabled_not_ready_and_the_latest_snapshot()
    {
        var time = Make.Clock();
        using var monitor = Make.Monitor(new FakeReadings(), time);
        var live = new KodoAdminSource(monitor, time);

        var before = await live.GetAsync(Ct);
        Assert.Null(before.Score);
        Assert.Equal("unknown", before.Checks[0].State);

        await monitor.RunCycleAsync(Ct);
        var after = await live.GetAsync(Ct);
        Assert.Equal(100, after.Score);
        Assert.Equal("healthy", after.Checks[0].State);
        Assert.Contains(after.Checks, c => c.Name == "host.disk.free.data" && c.State == "healthy");

        var off = await new KodoAdminSource(new DisabledKodoStatusSource(), time).GetAsync(Ct);
        Assert.Equal("disabled", off.Checks[0].State);
    }

    [Fact]
    public async Task The_monitor_writes_the_snapshot_file_after_each_pass_and_the_file_round_trips()
    {
        var time = Make.Clock();
        var path = Path.Combine(_dir, "nested", KodoSnapshotFile.FileName);
        using var monitor = Make.Monitor(new FakeReadings(), time, options: new KodoServerOptions { SnapshotFile = path });

        await monitor.RunCycleAsync(Ct);

        var read = KodoSnapshotFile.TryRead(path);
        Assert.NotNull(read);
        Assert.Equal(monitor.Latest!.Revision, read.Revision);
        Assert.Equal("healthy", read.Status.State);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task An_unwritable_snapshot_path_does_not_stop_the_pass()
    {
        var time = Make.Clock();
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "file");
        await File.WriteAllTextAsync(blocker, "x", Ct);
        using var monitor = Make.Monitor(new FakeReadings(), time, options: new KodoServerOptions { SnapshotFile = Path.Combine(blocker, "kodo.json") });

        await monitor.RunCycleAsync(Ct);

        Assert.NotNull(monitor.Latest);
    }

    [Fact]
    public void Reading_a_missing_or_garbage_file_is_null()
    {
        Directory.CreateDirectory(_dir);
        var garbage = Path.Combine(_dir, "g.json");
        File.WriteAllText(garbage, "not json");

        Assert.Null(KodoSnapshotFile.TryRead(Path.Combine(_dir, "none.json")));
        Assert.Null(KodoSnapshotFile.TryRead(garbage));
    }

    [Fact]
    public void Path_resolution_prefers_the_explicit_path_then_the_data_directory_then_the_default()
    {
        Assert.Equal("/x/y.json", KodoSnapshotFile.ResolvePath(" /x/y.json ", "/d"));
        Assert.Equal(Path.Combine("/d", KodoSnapshotFile.FileName), KodoSnapshotFile.ResolvePath(null, "/d"));
        Assert.Equal(KodoSnapshotFile.DefaultPath(), KodoSnapshotFile.ResolvePath(" ", null));
    }

    [Fact]
    public void Composition_registers_the_admin_port_and_the_snapshot_file_option_follows_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bazaar:Kodo:DataDirectory"] = _dir,
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddBazaarKodo(config);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<KodoAdminSource>(provider.GetRequiredService<IAdminKodoSource>());
        Assert.Equal(Path.Combine(_dir, KodoSnapshotFile.FileName), provider.GetRequiredService<KodoServerOptions>().SnapshotFile);

        var off = KodoServiceCollectionExtensions.ReadOptions(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Bazaar:Kodo:SnapshotFile"] = string.Empty }).Build().GetSection("Bazaar:Kodo"));
        Assert.Null(off.SnapshotFile);
        Assert.Null(KodoServiceCollectionExtensions.ReadOptions(null).SnapshotFile);
    }
}
