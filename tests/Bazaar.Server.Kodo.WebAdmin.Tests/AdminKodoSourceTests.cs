using Bazaar.Contracts.Kodo;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Kodo.WebAdmin.Tests;

public class AdminKodoSourceTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeIncidents(params KodoIncidentSummary[] items) : IKodoIncidentSource
    {
        public Task<IReadOnlyList<KodoIncidentSummary>> ListOpenAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<KodoIncidentSummary>>(items);
    }

    private static AdminKodoSource Source(KodoStatusSnapshotDto? snapshot, params KodoIncidentSummary[] incidents)
    {
        var holder = new KodoSnapshotHolder();
        if (snapshot is not null)
        {
            holder.Publish(snapshot);
        }

        return new AdminKodoSource(holder, new FakeIncidents(incidents));
    }

    private static KodoStatusSnapshotDto Snapshot(string state, int score, string? underlying = null, params KodoComponentDto[] components) =>
        new(KodoStatusSnapshotDto.CurrentSchemaVersion, 7, new KodoStatusDto(state, underlying, score, _t0, []), "ab".PadRight(64, 'c'), components);

    [Fact]
    public async Task No_data_yet_gives_null_score_and_empty_lists()
    {
        AdminKodoView view = await Source(null).GetAsync(TestContext.Current.CancellationToken);
        Assert.Null(view.Score);
        Assert.Empty(view.Checks);
        Assert.Empty(view.Incidents);
    }

    [Fact]
    public async Task Healthy_snapshot_maps_score_and_components_to_checks()
    {
        var snap = Snapshot("healthy", 98, null,
            new KodoComponentDto("db.ping", "green", null, _t0),
            new KodoComponentDto("host.disk.free", "green", null, _t0));
        AdminKodoView view = await Source(snap).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(98, view.Score);
        Assert.Equal(["kodo.state", "db.ping", "host.disk.free"], view.Checks.Select(c => c.Name));
        Assert.Equal(["healthy", "green", "green"], view.Checks.Select(c => c.State));
        Assert.All(view.Checks, c => Assert.Null(c.Detail));
        Assert.Empty(view.Incidents);
    }

    [Fact]
    public async Task Degraded_snapshot_carries_cause_codes_as_detail()
    {
        var snap = Snapshot("degraded", 72, null,
            new KodoComponentDto("db.ping", "green", null, _t0),
            new KodoComponentDto("host.disk.free", "amber", "disk.low", _t0),
            new KodoComponentDto("mail.relay", "unknown", "probe.timeout", _t0));
        AdminKodoView view = await Source(snap).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(72, view.Score);
        Assert.Equal("degraded", view.Checks[0].State);
        Assert.Equal(("amber", "disk.low"), (view.Checks[2].State, view.Checks[2].Detail));
        Assert.Equal(("unknown", "probe.timeout"), (view.Checks[3].State, view.Checks[3].Detail));
    }

    [Fact]
    public async Task Frozen_state_names_the_underlying_state()
    {
        AdminKodoView view = await Source(Snapshot("frozen", 40, "failing")).GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(("frozen", "underlying: failing"), (view.Checks[0].State, view.Checks[0].Detail));
    }

    [Fact]
    public async Task View_never_exposes_the_journal_head()
    {
        AdminKodoView view = await Source(Snapshot("healthy", 99)).GetAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("abcc", System.Text.Json.JsonSerializer.Serialize(view.Checks));
    }

    [Fact]
    public async Task Incidents_map_with_and_without_runbook_newest_first()
    {
        var older = new KodoIncidentSummary("inc-1", "  Disk nearly full  ", "warning", _t0.AddMinutes(-30), null);
        var newer = new KodoIncidentSummary("inc-2", "Database down", "critical", _t0, "https://runbooks.example/db-down");
        AdminKodoView view = await Source(Snapshot("failing", 30), older, newer).GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["inc-2", "inc-1"], view.Incidents.Select(i => i.Id));
        Assert.Equal("critical", view.Incidents[0].Severity);
        Assert.Equal("https://runbooks.example/db-down", view.Incidents[0].RunbookLink);
        Assert.Equal("Disk nearly full", view.Incidents[1].Title);
        Assert.Null(view.Incidents[1].RunbookLink);
    }

    [Fact]
    public async Task Incidents_show_without_a_snapshot()
    {
        var inc = new KodoIncidentSummary("inc-1", "Kodo restarted", "warning", _t0, null);
        AdminKodoView view = await Source(null, inc).GetAsync(TestContext.Current.CancellationToken);
        Assert.Null(view.Score);
        Assert.Single(view.Incidents);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/x")]
    [InlineData("C:\\runbooks\\x")]
    [InlineData("runbooks/x")]
    [InlineData("   ")]
    public void Unsafe_runbook_links_are_dropped(string link) => Assert.Null(AdminKodoSource.SafeLink(link));

    [Theory]
    [InlineData("/runbooks/db-down")]
    [InlineData("http://runbooks.example/x")]
    [InlineData("https://runbooks.example/x")]
    public void Safe_runbook_links_pass(string link) => Assert.Equal(link, AdminKodoSource.SafeLink(link));

    [Fact]
    public async Task Long_titles_are_cut_and_the_list_is_capped()
    {
        var items = Enumerable.Range(0, 80)
            .Select(i => new KodoIncidentSummary($"i{i:D2}", new string('x', 500), "warning", _t0.AddMinutes(i), null)).ToArray();
        AdminKodoView view = await Source(null, items).GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AdminKodoSource.MaxIncidents, view.Incidents.Count);
        Assert.All(view.Incidents, i => Assert.Equal(AdminKodoSource.MaxTitleLength, i.Title.Length));
    }

    [Fact]
    public async Task Registration_wires_the_port_to_the_holder()
    {
        var services = new ServiceCollection().AddBazaarKodo().AddBazaarKodoWebAdmin().BuildServiceProvider();
        services.GetRequiredService<IKodoSnapshotPublisher>().Publish(Snapshot("healthy", 91));
        AdminKodoView view = await services.GetRequiredService<IAdminKodoSource>().GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(91, view.Score);
        Assert.Equal("kodo-web-admin", new KodoWebAdminComponent().Manifest.Id);
    }
}
