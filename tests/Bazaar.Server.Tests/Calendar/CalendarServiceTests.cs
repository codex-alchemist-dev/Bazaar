using System.Text;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Calendar;
using Bazaar.Server.Gc;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Calendar;

/// <summary>
/// <see cref="CalendarService"/> against a real PostgreSQL: create, update, confirm and cancel; one GC line per change; the
/// rejection path; the sealed title. Time is a fake clock.
/// </summary>
public sealed class CalendarServiceTests(CalendarDb db) : IClassFixture<CalendarDb>
{
    private static readonly DateTimeOffset _now = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _start = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private readonly CalendarActor _owner = new(AuthorKind.Owner, Guid.CreateVersion7());

    private CalendarService Service(CalendarPolicy? policy = null) =>
        new(db.DataSource, new ReversingBodyCodec(), db.GcWriter(new FakeTimeProvider(_now)), policy);

    private static CalendarDraft Vacation(Guid agent, string title = "Leave", int days = 3, DateTimeOffset? start = null) => new()
    {
        Kind = CalendarKind.Vacation,
        Title = title,
        StartsAt = start ?? _start,
        EndsAt = (start ?? _start).AddDays(days),
        Tz = "Europe/London",
        AgentId = agent,
    };

    private static CalendarDraft Release(string title = "v1.0 cut", Guid? project = null) => new()
    {
        Kind = CalendarKind.Release,
        Title = title,
        StartsAt = _start,
        EndsAt = _start.AddDays(1),
        Tz = "Europe/London",
        ProjectId = project,
    };

    private static CalendarActor Agent(Guid id) => new(AuthorKind.Agent, id);

    private async Task<long> EventCountAsync(Guid agent) =>
        await db.ScalarAsync<long>("SELECT count(*) FROM calendar_event WHERE agent_id = $1", agent);

    private async Task<string> AuthorKindOfLineAsync(Guid gc, Guid eventId) =>
        await db.ScalarAsync<string>(
            "SELECT author_kind FROM gc_event WHERE gc_id = $1 AND kind = 'system' AND payload->>'event_id' = $2 ORDER BY seq LIMIT 1",
            gc,
            eventId.ToString());

    [Fact]
    public async Task An_agent_request_is_proposed_and_writes_one_line_with_a_sealed_title()
    {
        var agent = await db.CreateAgentAsync("requester");
        var gc = await db.CreateGcAsync();
        var result = await Service().CreateAsync(Vacation(agent, title: "Leave"), Agent(agent), gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Applied, result.Outcome);
        var row = result.Event!;
        Assert.Equal(CalendarStatus.Proposed, row.Status);
        Assert.Equal(1L, row.Version);
        Assert.True(row.AffectsAvailability);

        var lines = await db.GcLinesAsync(gc, row.Id);
        var line = Assert.Single(lines);
        Assert.Equal(("created", 1L), (line.Change, line.Version));
        Assert.Equal($"calendar:{row.Id:D}:1:created", line.ClientMsgId);
        Assert.Equal("agent", await AuthorKindOfLineAsync(gc, row.Id));

        // The stored title is not the plain text, and the service opens it again.
        var raw = await db.ScalarAsync<byte[]>("SELECT title_ct FROM calendar_event WHERE id = $1", row.Id);
        Assert.NotEqual(Encoding.UTF8.GetBytes("Leave"), raw);
        var service = Service();
        Assert.Equal("Leave", service.OpenTitle(row));
    }

    [Fact]
    public async Task Owner_confirms_then_updates_and_each_change_writes_its_line()
    {
        var agent = await db.CreateAgentAsync("confirm-update");
        var gc = await db.CreateGcAsync();
        var service = Service();
        var created = await service.CreateAsync(Vacation(agent), Agent(agent), gc, TestContext.Current.CancellationToken);
        var id = created.Event!.Id;

        var confirmed = await service.ConfirmAsync(id, expectedVersion: 1, _owner, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Applied, confirmed.Outcome);
        Assert.Equal(CalendarStatus.Confirmed, confirmed.Event!.Status);
        Assert.Equal(2L, confirmed.Event.Version);

        var updated = await service.UpdateAsync(id, expectedVersion: 2, Vacation(agent, title: "Leave, longer", days: 5), _owner, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Applied, updated.Outcome);
        Assert.Equal(3L, updated.Event!.Version);
        Assert.Equal(CalendarStatus.Confirmed, updated.Event.Status);
        Assert.Equal(_start.AddDays(5), updated.Event.EndsAt);
        Assert.Equal("Leave, longer", service.OpenTitle(updated.Event));

        var lines = await db.GcLinesAsync(gc, id);
        Assert.Equal(new[] { ("created", 1L), ("confirmed", 2L), ("updated", 3L) }, lines.Select(l => (l.Change, l.Version)));
        Assert.Equal("owner", await AuthorKindOfLineAsync(gc, id));
    }

    [Fact]
    public async Task A_stale_version_is_a_conflict_and_changes_nothing()
    {
        var agent = await db.CreateAgentAsync("stale");
        var gc = await db.CreateGcAsync();
        var service = Service();
        var id = (await service.CreateAsync(Vacation(agent), _owner, gc, TestContext.Current.CancellationToken)).Event!.Id;

        var result = await service.UpdateAsync(id, expectedVersion: 0, Vacation(agent, title: "Too late"), _owner, gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Conflict, result.Outcome);
        Assert.Equal(1L, result.Event!.Version);
        Assert.Equal("Leave", service.OpenTitle((await service.GetAsync(id, TestContext.Current.CancellationToken))!));
        Assert.Single(await db.GcLinesAsync(gc, id));
    }

    [Fact]
    public async Task An_agent_cannot_confirm_its_own_request()
    {
        var agent = await db.CreateAgentAsync("self-confirm");
        var gc = await db.CreateGcAsync();
        var service = Service();
        var id = (await service.CreateAsync(Vacation(agent), Agent(agent), gc, TestContext.Current.CancellationToken)).Event!.Id;

        var result = await service.ConfirmAsync(id, 1, Agent(agent), gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Refused, result.Outcome);
        Assert.Equal(CalendarStatus.Proposed, (await service.GetAsync(id, TestContext.Current.CancellationToken))!.Status);
        Assert.Single(await db.GcLinesAsync(gc, id));
    }

    [Fact]
    public async Task An_agent_cannot_request_a_vacation_for_another_agent()
    {
        var agent = await db.CreateAgentAsync("victim");
        var other = await db.CreateAgentAsync("requester-b");
        var gc = await db.CreateGcAsync();

        var result = await Service().CreateAsync(Vacation(agent), Agent(other), gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Refused, result.Outcome);
        Assert.Equal(0L, await EventCountAsync(agent));
    }

    [Fact]
    public async Task An_agent_cannot_create_a_release_but_a_project_lead_can_under_the_default_policy()
    {
        var agent = await db.CreateAgentAsync("release-agent");
        var gc = await db.CreateGcAsync();
        var service = Service();
        var lead = new CalendarActor(AuthorKind.Human, Guid.CreateVersion7(), IsProjectLead: true);

        var project = Guid.CreateVersion7();
        var refused = await service.CreateAsync(Release(project: project), Agent(agent), gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Refused, refused.Outcome);

        var applied = await service.CreateAsync(Release(project: project), lead, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Applied, applied.Outcome);
        Assert.Equal(CalendarStatus.Confirmed, applied.Event!.Status);
        Assert.Equal("human", await AuthorKindOfLineAsync(gc, applied.Event.Id));
    }

    [Fact]
    public async Task With_release_creators_set_to_owner_a_project_lead_is_refused()
    {
        var gc = await db.CreateGcAsync();
        var service = Service(new CalendarPolicy { ReleaseCreators = ReleaseCreators.Owner });
        var lead = new CalendarActor(AuthorKind.Human, Guid.CreateVersion7(), IsProjectLead: true);

        var project = Guid.CreateVersion7();
        var result = await service.CreateAsync(Release(project: project), lead, gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Refused, result.Outcome);
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM calendar_event WHERE project_id = $1", project));
    }

    [Fact]
    public async Task An_inverted_range_is_rejected_before_anything_is_written()
    {
        var agent = await db.CreateAgentAsync("inverted");
        var gc = await db.CreateGcAsync();
        var draft = Vacation(agent) with { EndsAt = _start.AddDays(-1) };

        var result = await Service().CreateAsync(draft, _owner, gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Invalid, result.Outcome);
        Assert.Equal(0L, await EventCountAsync(agent));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", gc));
    }

    [Fact]
    public async Task A_vacation_without_an_agent_and_an_unknown_zone_are_rejected()
    {
        var agent = await db.CreateAgentAsync("bad-input");
        var gc = await db.CreateGcAsync();
        var service = Service();

        var noAgent = await service.CreateAsync(Vacation(agent) with { AgentId = null }, _owner, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Invalid, noAgent.Outcome);

        var badZone = await service.CreateAsync(Vacation(agent) with { Tz = "Nowhere/Town" }, _owner, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Invalid, badZone.Outcome);

        Assert.Equal(0L, await EventCountAsync(agent));
    }

    [Fact]
    public async Task An_owner_created_vacation_starts_confirmed()
    {
        var agent = await db.CreateAgentAsync("owner-made");
        var gc = await db.CreateGcAsync();

        var result = await Service().CreateAsync(Vacation(agent), _owner, gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarStatus.Confirmed, result.Event!.Status);
    }

    [Fact]
    public async Task Cancel_is_a_status_change_and_a_cancelled_event_cannot_change_again()
    {
        var agent = await db.CreateAgentAsync("canceller");
        var gc = await db.CreateGcAsync();
        var service = Service();
        var id = (await service.CreateAsync(Vacation(agent), _owner, gc, TestContext.Current.CancellationToken)).Event!.Id;

        var cancelled = await service.CancelAsync(id, 1, _owner, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Applied, cancelled.Outcome);
        Assert.Equal(CalendarStatus.Cancelled, cancelled.Event!.Status);
        Assert.Equal(2L, cancelled.Event.Version);
        Assert.Equal(1L, await EventCountAsync(agent));

        var again = await service.CancelAsync(id, 2, _owner, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Invalid, again.Outcome);

        var changed = await service.UpdateAsync(id, 2, Vacation(agent, title: "Back on"), _owner, gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Invalid, changed.Outcome);

        var lines = await db.GcLinesAsync(gc, id);
        Assert.Equal(new[] { ("created", 1L), ("cancelled", 2L) }, lines.Select(l => (l.Change, l.Version)));
    }

    [Fact]
    public async Task An_agent_may_cancel_its_own_vacation_but_not_another_agents()
    {
        var mine = await db.CreateAgentAsync("cancel-mine");
        var theirs = await db.CreateAgentAsync("cancel-theirs");
        var gc = await db.CreateGcAsync();
        var service = Service();
        var id = (await service.CreateAsync(Vacation(mine), Agent(mine), gc, TestContext.Current.CancellationToken)).Event!.Id;

        var refused = await service.CancelAsync(id, 1, Agent(theirs), gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Refused, refused.Outcome);

        var applied = await service.CancelAsync(id, 1, Agent(mine), gc, TestContext.Current.CancellationToken);
        Assert.Equal(CalendarOutcome.Applied, applied.Outcome);
    }

    [Fact]
    public async Task An_all_day_release_across_the_spring_clock_change_keeps_its_real_length()
    {
        var gc = await db.CreateGcAsync();
        var start = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var draft = new CalendarDraft
        {
            Kind = CalendarKind.Release,
            Title = "Clocks forward",
            StartsAt = start,
            EndsAt = new DateTimeOffset(2026, 3, 29, 23, 0, 0, TimeSpan.Zero),
            AllDay = true,
            Tz = "Europe/London",
        };

        var result = await Service().CreateAsync(draft, _owner, gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Applied, result.Outcome);
        Assert.Equal(TimeSpan.FromHours(23), result.Event!.EndsAt - result.Event.StartsAt);
        Assert.True(result.Event.AllDay);
        Assert.Equal("Europe/London", result.Event.Tz);
    }

    [Fact]
    public async Task An_all_day_event_that_is_not_at_local_midnight_is_rejected()
    {
        var gc = await db.CreateGcAsync();
        var draft = new CalendarDraft
        {
            Kind = CalendarKind.Milestone,
            Title = "Off-grid",
            StartsAt = _start.AddHours(9),
            EndsAt = _start.AddDays(1),
            AllDay = true,
            Tz = "Europe/London",
        };

        var result = await Service().CreateAsync(draft, _owner, gc, TestContext.Current.CancellationToken);

        Assert.Equal(CalendarOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task Listing_returns_only_overlapping_events_and_filters_by_agent()
    {
        var a = await db.CreateAgentAsync("list-a");
        var b = await db.CreateAgentAsync("list-b");
        var gc = await db.CreateGcAsync();
        var service = Service();
        var early = (await service.CreateAsync(Vacation(a, days: 2), _owner, gc, TestContext.Current.CancellationToken)).Event!.Id;
        var late = (await service.CreateAsync(Vacation(a, days: 2, start: _start.AddDays(10)), _owner, gc, TestContext.Current.CancellationToken)).Event!.Id;
        var other = (await service.CreateAsync(Vacation(b, days: 5), _owner, gc, TestContext.Current.CancellationToken)).Event!.Id;

        var forA = await service.ListOverlappingAsync(_start.AddDays(1), _start.AddDays(3), a, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { early }, forA.Select(e => e.Id));

        var everyone = await service.ListOverlappingAsync(_start.AddDays(1), _start.AddDays(11), null, TestContext.Current.CancellationToken);
        Assert.Equal(new HashSet<Guid> { early, late, other }, everyone.Select(e => e.Id).ToHashSet());
    }

    [Fact]
    public async Task A_failed_notice_leaves_the_change_committed_and_announce_adds_the_line_once()
    {
        var agent = await db.CreateAgentAsync("notice-retry");
        var realGc = await db.CreateGcAsync();
        var missingGc = Guid.CreateVersion7();
        var service = Service();

        await Assert.ThrowsAsync<GcNotFoundException>(() => service.CreateAsync(Vacation(agent), _owner, missingGc, TestContext.Current.CancellationToken));

        // The change committed before the line failed.
        Assert.Equal(1L, await EventCountAsync(agent));
        var row = await db.ScalarAsync<Guid>("SELECT id FROM calendar_event WHERE agent_id = $1", agent);
        var stored = (await service.GetAsync(row, TestContext.Current.CancellationToken))!;

        await service.AnnounceAsync(stored, CalendarChangeKind.Created, _owner, realGc, TestContext.Current.CancellationToken);
        await service.AnnounceAsync(stored, CalendarChangeKind.Created, _owner, realGc, TestContext.Current.CancellationToken);

        var lines = await db.GcLinesAsync(realGc, row);
        Assert.Single(lines);
    }
}
