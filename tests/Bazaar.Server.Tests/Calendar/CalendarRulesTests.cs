using Bazaar.Contracts.Gc;
using Bazaar.Server.Calendar;

namespace Bazaar.Server.Tests.Calendar;

/// <summary>Pure calendar rules (docs/design/24 section 4). No database: these run anywhere.</summary>
public sealed class CalendarRulesTests
{
    private static readonly Guid _agent = Guid.Parse("00000000-0000-7000-8000-0000000000a1");
    private static readonly Guid _otherAgent = Guid.Parse("00000000-0000-7000-8000-0000000000a2");
    private static readonly CalendarActor _owner = new(AuthorKind.Owner, Guid.Parse("00000000-0000-7000-8000-00000000000f"));
    private static readonly CalendarActor _self = new(AuthorKind.Agent, _agent);
    private static readonly CalendarActor _stranger = new(AuthorKind.Agent, _otherAgent);
    private static readonly CalendarActor _lead = new(AuthorKind.Human, Guid.Parse("00000000-0000-7000-8000-0000000000b1"), IsProjectLead: true);

    private static readonly DateTimeOffset _start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static CalendarDraft Draft(
        CalendarKind kind = CalendarKind.Vacation,
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        bool allDay = false,
        string tz = "Europe/London",
        string title = "Leave",
        Guid? agent = null) => new()
    {
        Kind = kind,
        Title = title,
        StartsAt = start ?? _start,
        EndsAt = end ?? _start.AddDays(3),
        AllDay = allDay,
        Tz = tz,
        AgentId = kind == CalendarKind.Vacation ? agent ?? _agent : agent,
    };

    [Fact]
    public void A_valid_vacation_draft_passes()
    {
        Assert.Null(CalendarRules.ValidateDraft(Draft()));
    }

    [Fact]
    public void Inverted_range_is_refused()
    {
        var reason = CalendarRules.ValidateDraft(Draft(start: _start, end: _start.AddHours(-1)));
        Assert.NotNull(reason);
        Assert.Contains("end after", reason);
    }

    [Fact]
    public void Zero_length_range_is_refused()
    {
        Assert.NotNull(CalendarRules.ValidateDraft(Draft(start: _start, end: _start)));
    }

    [Fact]
    public void Unknown_time_zone_is_refused()
    {
        Assert.Contains("Unknown time zone", CalendarRules.ValidateDraft(Draft(tz: "Mars/Olympus_Mons")));
    }

    [Fact]
    public void Blank_title_and_long_title_are_refused()
    {
        Assert.NotNull(CalendarRules.ValidateDraft(Draft(title: "   ")));
        Assert.NotNull(CalendarRules.ValidateDraft(Draft(title: new string('x', CalendarRules.MaxTitleLength + 1))));
        Assert.Null(CalendarRules.ValidateDraft(Draft(title: new string('x', CalendarRules.MaxTitleLength))));
    }

    [Fact]
    public void Vacation_without_an_agent_is_refused()
    {
        var draft = Draft() with { AgentId = null };
        Assert.Contains("must name an agent", CalendarRules.ValidateDraft(draft));
    }

    [Fact]
    public void All_day_event_must_start_and_end_at_local_midnight()
    {
        var londonMidnight = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(CalendarRules.ValidateDraft(Draft(kind: CalendarKind.Release, allDay: true, start: londonMidnight, end: londonMidnight.AddDays(1), agent: null)));
        Assert.NotNull(CalendarRules.ValidateDraft(Draft(kind: CalendarKind.Release, allDay: true, start: londonMidnight.AddHours(9), end: londonMidnight.AddDays(1), agent: null)));
    }

    [Fact]
    public void All_day_event_across_the_spring_daylight_saving_change_is_valid()
    {
        // 2026-03-29 is the UK clocks-forward day: the local day is 23 hours long, and both edges are still local midnight.
        // Local midnight on 29 March is GMT (00:00 UTC); local midnight on 30 March is BST (23:00 UTC on 29 March).
        var midnightBefore = new DateTimeOffset(2026, 3, 29, 0, 0, 0, TimeSpan.Zero);
        var midnightAfter = new DateTimeOffset(2026, 3, 29, 23, 0, 0, TimeSpan.Zero);
        var draft = Draft(kind: CalendarKind.Release, allDay: true, start: midnightBefore, end: midnightAfter, tz: "Europe/London", agent: null);
        Assert.Null(CalendarRules.ValidateDraft(draft));
        Assert.Equal(TimeSpan.FromHours(23), midnightAfter - midnightBefore);
    }

    [Fact]
    public void All_day_event_in_a_zone_far_from_utc_is_valid_at_its_local_midnight()
    {
        // Auckland is UTC+13 in late December; local midnight on 24 December is 11:00 UTC on 23 December.
        var localMidnight = new DateTimeOffset(2026, 12, 23, 11, 0, 0, TimeSpan.Zero);
        var draft = Draft(kind: CalendarKind.Milestone, allDay: true, start: localMidnight, end: localMidnight.AddDays(1), tz: "Pacific/Auckland", agent: null);
        Assert.Null(CalendarRules.ValidateDraft(draft));
    }

    [Fact]
    public void Every_vacation_blocks_availability_and_other_kinds_only_when_the_policy_lists_them()
    {
        var policy = new CalendarPolicy();
        Assert.True(CalendarRules.AffectsAvailability(CalendarKind.Vacation, policy));
        Assert.False(CalendarRules.AffectsAvailability(CalendarKind.Release, policy));

        var listed = new CalendarPolicy { AvailabilityKinds = new HashSet<CalendarKind> { CalendarKind.Freeze } };
        Assert.True(CalendarRules.AffectsAvailability(CalendarKind.Freeze, listed));
        Assert.False(CalendarRules.AffectsAvailability(CalendarKind.Release, listed));
    }

    [Fact]
    public void Owner_may_create_every_kind()
    {
        var policy = new CalendarPolicy();
        foreach (var kind in Enum.GetValues<CalendarKind>())
            Assert.True(CalendarRules.MayCreate(_owner, kind, _agent, policy));
    }

    [Fact]
    public void An_agent_may_request_only_its_own_vacation()
    {
        var policy = new CalendarPolicy();
        Assert.True(CalendarRules.MayCreate(_self, CalendarKind.Vacation, _agent, policy));
        Assert.False(CalendarRules.MayCreate(_stranger, CalendarKind.Vacation, _agent, policy));
        Assert.False(CalendarRules.MayCreate(_self, CalendarKind.Release, null, policy));
        Assert.False(CalendarRules.MayCreate(_self, CalendarKind.Planning, null, policy));
    }

    [Fact]
    public void Release_creators_follow_the_setting()
    {
        var defaults = new CalendarPolicy();
        Assert.True(CalendarRules.MayCreate(_lead, CalendarKind.Release, null, defaults));
        Assert.True(CalendarRules.MayCreate(_lead, CalendarKind.Milestone, null, defaults));

        var ownerOnly = new CalendarPolicy { ReleaseCreators = ReleaseCreators.Owner };
        Assert.False(CalendarRules.MayCreate(_lead, CalendarKind.Release, null, ownerOnly));
        Assert.True(CalendarRules.MayCreate(_owner, CalendarKind.Release, null, ownerOnly));
    }

    [Fact]
    public void Planning_and_freeze_are_owner_only_in_this_build()
    {
        var policy = new CalendarPolicy();
        Assert.False(CalendarRules.MayCreate(_lead, CalendarKind.Planning, null, policy));
        Assert.False(CalendarRules.MayCreate(_lead, CalendarKind.Freeze, null, policy));
    }

    [Fact]
    public void A_vacation_starts_confirmed_for_the_owner_and_proposed_for_an_agent_request()
    {
        var policy = new CalendarPolicy();
        Assert.Equal(CalendarStatus.Confirmed, CalendarRules.InitialStatus(_owner, CalendarKind.Vacation, policy));
        Assert.Equal(CalendarStatus.Proposed, CalendarRules.InitialStatus(_self, CalendarKind.Vacation, policy));
        Assert.Equal(CalendarStatus.Confirmed, CalendarRules.InitialStatus(_self, CalendarKind.Release, policy));
    }

    [Fact]
    public void Vacation_confirmer_follows_the_setting()
    {
        var policy = new CalendarPolicy();
        Assert.False(CalendarRules.MayConfirm(_lead, CalendarKind.Vacation, policy));
        Assert.False(CalendarRules.MayConfirm(_self, CalendarKind.Vacation, policy));
        Assert.True(CalendarRules.MayConfirm(_owner, CalendarKind.Vacation, policy));

        var leadConfirms = new CalendarPolicy { VacationConfirmer = VacationConfirmer.ProjectLead };
        Assert.True(CalendarRules.MayConfirm(_lead, CalendarKind.Vacation, leadConfirms));
        Assert.Equal(CalendarStatus.Confirmed, CalendarRules.InitialStatus(_lead, CalendarKind.Vacation, leadConfirms));
    }

    [Fact]
    public void An_agent_may_change_its_own_proposed_vacation_but_not_a_confirmed_one()
    {
        var policy = new CalendarPolicy();
        Assert.True(CalendarRules.MayModify(_self, CalendarKind.Vacation, _agent, CalendarStatus.Proposed, policy));
        Assert.False(CalendarRules.MayModify(_self, CalendarKind.Vacation, _agent, CalendarStatus.Confirmed, policy));
        Assert.False(CalendarRules.MayModify(_stranger, CalendarKind.Vacation, _agent, CalendarStatus.Proposed, policy));
    }

    [Fact]
    public void Cancel_is_allowed_for_the_owner_the_vacation_agent_and_release_creators()
    {
        var policy = new CalendarPolicy();
        Assert.True(CalendarRules.MayCancel(_owner, CalendarKind.Vacation, _agent, policy));
        Assert.True(CalendarRules.MayCancel(_self, CalendarKind.Vacation, _agent, policy));
        Assert.False(CalendarRules.MayCancel(_stranger, CalendarKind.Vacation, _agent, policy));
        Assert.True(CalendarRules.MayCancel(_lead, CalendarKind.Release, null, policy));
        Assert.False(CalendarRules.MayCancel(_lead, CalendarKind.Freeze, null, policy));
    }

    [Fact]
    public void Kind_and_status_text_round_trip_and_match_the_check_constraints()
    {
        foreach (var kind in Enum.GetValues<CalendarKind>())
            Assert.Equal(kind, CalendarRules.KindFromSql(CalendarRules.ToSql(kind)));
        foreach (var status in Enum.GetValues<CalendarStatus>())
            Assert.Equal(status, CalendarRules.StatusFromSql(CalendarRules.ToSql(status)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CalendarRules.KindFromSql("meeting"));
    }

    [Fact]
    public void Calendar_actors_map_to_a_gc_source_trust_and_others_are_refused()
    {
        Assert.Equal(SourceTrust.Owner, CalendarRules.SourceTrustFor(AuthorKind.Owner));
        Assert.Equal(SourceTrust.Agent, CalendarRules.SourceTrustFor(AuthorKind.Agent));
        Assert.Equal(SourceTrust.MemberHuman, CalendarRules.SourceTrustFor(AuthorKind.Human));
        Assert.Equal(SourceTrust.System, CalendarRules.SourceTrustFor(AuthorKind.System));
        Assert.Throws<ArgumentException>(() => CalendarRules.SourceTrustFor(AuthorKind.ExternalBridge));
    }
}
