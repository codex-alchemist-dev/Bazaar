using Bazaar.Server.Kanban.Ryusei;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The RyuseiFuda creation rules, the authority matrix and the cap arithmetic (doc 38, Authority and Loop and runaway
/// safeguards) as pure functions, plus the refusal path of <see cref="RyuseiTriggerService"/> with no database.
/// </summary>
public sealed class RyuseiTriggerServiceRuleTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _project = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _otherProject = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid _self = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _coworker = Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid _stranger = Guid.Parse("cccccccc-cccc-7ccc-8ccc-cccccccccccc");
    private static readonly Guid _owner = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");

    private static readonly RyuseiLimits _limits = new();

    private static RyuseiTargetInfo Target(Guid agent, Guid project, bool active = true) => new(agent, project, active);

    private static RyuseiActor Agent(Guid id) => RyuseiActor.ForAgent(id, _project);

    private static RyuseiTimerTriggerSpec Timer() => new(_now.AddHours(1));

    private static RyuseiIntervalTriggerSpec Interval(TimeSpan every) => new(every, _now);

    private static RyuseiWatcherTriggerSpec Watcher(string kind) => new(
        new RyuseiPredicate { Kind = kind }, PollSeconds: 60, DebounceSeconds: 30, ExpiresAt: null, MaxFires: null);

    private static RyuseiCreateRequest Request(
        RyuseiActor creator,
        IReadOnlyList<RyuseiTargetInfo> targets,
        RyuseiTriggerSpec? spec = null,
        RyuseiPingMode ping = RyuseiPingMode.None,
        Guid? attachGc = null,
        int chainDepth = 0,
        RyuseiCoworkerTriggers coworker = RyuseiCoworkerTriggers.Allowed,
        string purpose = "check the build",
        RyuseiTriggerKind? kind = null)
    {
        var effectiveSpec = spec ?? Timer();
        return new RyuseiCreateRequest(
            creator, _project, targets, kind ?? effectiveSpec.Kind, effectiveSpec, purpose, attachGc, ping, chainDepth, coworker);
    }

    private static RyuseiRefusal Check(RyuseiCreateRequest request, RyuseiLimits? limits = null) =>
        RyuseiTriggerRules.CheckCreate(request, limits ?? _limits).Refusal;

    // Authority: creation.

    [Fact]
    public void Agent_may_target_itself()
    {
        var request = Request(Agent(_self), [Target(_self, _project)]);

        Assert.Equal(RyuseiRefusal.None, Check(request));
    }

    [Fact]
    public void Agent_may_target_a_coworker_in_the_same_project_by_default()
    {
        var request = Request(Agent(_self), [Target(_coworker, _project)]);

        Assert.Equal(RyuseiRefusal.None, Check(request));
    }

    [Fact]
    public void Coworker_trigger_is_refused_when_the_setting_is_ask_first()
    {
        var request = Request(Agent(_self), [Target(_coworker, _project)], coworker: RyuseiCoworkerTriggers.AskFirst);

        Assert.Equal(RyuseiRefusal.CoworkerTriggersNeedApproval, Check(request));
    }

    [Fact]
    public void Coworker_trigger_is_refused_when_the_setting_is_off()
    {
        var request = Request(Agent(_self), [Target(_coworker, _project)], coworker: RyuseiCoworkerTriggers.Off);

        Assert.Equal(RyuseiRefusal.CoworkerTriggersOff, Check(request));
    }

    [Fact]
    public void A_self_trigger_is_not_affected_by_the_coworker_setting()
    {
        var request = Request(Agent(_self), [Target(_self, _project)], coworker: RyuseiCoworkerTriggers.Off);

        Assert.Equal(RyuseiRefusal.None, Check(request));
    }

    [Fact]
    public void Agent_may_not_target_an_agent_in_another_project()
    {
        var request = Request(Agent(_self), [Target(_stranger, _otherProject)]);

        Assert.Equal(RyuseiRefusal.TargetOutsideProject, Check(request));
    }

    [Fact]
    public void Agent_may_not_target_a_resting_coworker()
    {
        var request = Request(Agent(_self), [Target(_coworker, _project, active: false)]);

        Assert.Equal(RyuseiRefusal.TargetNotActive, Check(request));
    }

    [Fact]
    public void An_agent_outside_the_project_may_not_create_a_trigger_in_it()
    {
        var creator = RyuseiActor.ForAgent(_self, _otherProject);

        Assert.Equal(RyuseiRefusal.TargetOutsideProject, Check(Request(creator, [Target(_self, _project)])));
    }

    [Fact]
    public void Owner_may_target_any_agent_in_the_project_including_a_resting_one()
    {
        var owner = RyuseiActor.ForOwner(_owner);

        Assert.Equal(RyuseiRefusal.None, Check(Request(owner, [Target(_stranger, _project, active: false)], coworker: RyuseiCoworkerTriggers.Off)));
    }

    [Fact]
    public void Owner_may_not_target_an_agent_in_another_project()
    {
        var owner = RyuseiActor.ForOwner(_owner);

        Assert.Equal(RyuseiRefusal.TargetOutsideProject, Check(Request(owner, [Target(_stranger, _otherProject)])));
    }

    // Authority: pause, resume and cancel.

    [Fact]
    public void The_target_agent_may_pause_and_resume()
    {
        Assert.True(RyuseiAuthority.CheckPauseOrResume(Agent(_coworker), [_self, _coworker]).IsAllowed);
    }

    [Fact]
    public void The_owner_may_pause_and_resume_any_trigger()
    {
        Assert.True(RyuseiAuthority.CheckPauseOrResume(RyuseiActor.ForOwner(_owner), [_self]).IsAllowed);
    }

    [Fact]
    public void A_creator_who_is_not_a_target_may_not_pause()
    {
        var check = RyuseiAuthority.CheckPauseOrResume(Agent(_self), [_coworker]);

        Assert.Equal(RyuseiRefusal.NotAuthorized, check.Refusal);
    }

    [Fact]
    public void A_stranger_may_not_pause_or_resume()
    {
        Assert.Equal(RyuseiRefusal.NotAuthorized, RyuseiAuthority.CheckPauseOrResume(Agent(_stranger), [_coworker]).Refusal);
    }

    [Fact]
    public void The_creator_and_the_owner_may_cancel()
    {
        Assert.True(RyuseiAuthority.CheckCancel(Agent(_self), _self).IsAllowed);
        Assert.True(RyuseiAuthority.CheckCancel(RyuseiActor.ForOwner(_owner), _self).IsAllowed);
    }

    [Fact]
    public void A_target_that_did_not_create_the_trigger_may_not_cancel_it()
    {
        Assert.Equal(RyuseiRefusal.NotAuthorized, RyuseiAuthority.CheckCancel(Agent(_coworker), _self).Refusal);
    }

    // Caps and shape.

    [Fact]
    public void Zero_targets_are_refused()
    {
        Assert.Equal(RyuseiRefusal.NoTargets, Check(Request(Agent(_self), [])));
    }

    [Theory]
    [InlineData(1, RyuseiRefusal.None)]
    [InlineData(8, RyuseiRefusal.None)]
    [InlineData(9, RyuseiRefusal.PingFanoutExceeded)]
    public void Fan_out_of_eight_is_allowed_and_nine_is_refused_at_creation(int count, RyuseiRefusal expected)
    {
        var targets = Enumerable.Range(0, count).Select(i => Target(Guid.CreateVersion7(), _project)).ToList();

        Assert.Equal(expected, Check(Request(Agent(_self), targets)));
    }

    [Fact]
    public void A_fan_out_setting_above_eight_cannot_loosen_the_cap()
    {
        var targets = Enumerable.Range(0, 9).Select(i => Target(Guid.CreateVersion7(), _project)).ToList();

        Assert.Equal(RyuseiRefusal.PingFanoutExceeded, Check(Request(Agent(_self), targets), new RyuseiLimits { MaxPingFanout = 20 }));
    }

    [Fact]
    public void The_same_target_named_twice_is_refused()
    {
        Assert.Equal(RyuseiRefusal.DuplicateTarget, Check(Request(Agent(_self), [Target(_coworker, _project), Target(_coworker, _project)])));
    }

    [Fact]
    public void Chain_depth_three_is_allowed_and_four_is_refused()
    {
        Assert.Equal(RyuseiRefusal.None, Check(Request(Agent(_self), [Target(_self, _project)], chainDepth: 3)));
        Assert.Equal(RyuseiRefusal.ChainDepthExceeded, Check(Request(Agent(_self), [Target(_self, _project)], chainDepth: 4)));
    }

    [Fact]
    public void A_negative_chain_depth_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(Request(Agent(_self), [Target(_self, _project)], chainDepth: -1)));
    }

    [Fact]
    public void An_interval_of_four_minutes_is_below_the_default_five_minute_floor()
    {
        var request = Request(Agent(_self), [Target(_self, _project)], spec: Interval(TimeSpan.FromMinutes(4)));

        Assert.Equal(RyuseiRefusal.IntervalBelowFloor, Check(request));
    }

    [Fact]
    public void An_interval_of_five_minutes_is_allowed_by_default()
    {
        var request = Request(Agent(_self), [Target(_self, _project)], spec: Interval(TimeSpan.FromMinutes(5)));

        Assert.Equal(RyuseiRefusal.None, Check(request));
    }

    [Fact]
    public void A_raised_minimum_interval_refuses_an_interval_that_the_default_allows()
    {
        var request = Request(Agent(_self), [Target(_self, _project)], spec: Interval(TimeSpan.FromMinutes(5)));

        Assert.Equal(RyuseiRefusal.IntervalBelowFloor, Check(request, new RyuseiLimits { MinInterval = TimeSpan.FromMinutes(10) }));
    }

    [Fact]
    public void An_unknown_predicate_kind_is_refused_as_an_invalid_predicate()
    {
        var request = Request(Agent(_self), [Target(_self, _project)], spec: Watcher("made.up"));

        Assert.Equal(RyuseiRefusal.InvalidPredicate, Check(request));
    }

    [Fact]
    public void A_known_predicate_kind_is_allowed()
    {
        var kind = RyuseiPredicates.WireName(RyuseiPredicateKind.TaskTransitioned);
        var request = Request(Agent(_self), [Target(_self, _project)], spec: Watcher(kind));

        Assert.Equal(RyuseiRefusal.None, Check(request));
    }

    [Fact]
    public void Attach_mode_without_a_gc_is_refused()
    {
        Assert.Equal(RyuseiRefusal.PingModeMismatch, Check(Request(Agent(_self), [Target(_self, _project)], ping: RyuseiPingMode.Attach)));
    }

    [Fact]
    public void Attach_mode_with_a_gc_is_allowed()
    {
        var request = Request(Agent(_self), [Target(_self, _project)], ping: RyuseiPingMode.Attach, attachGc: Guid.CreateVersion7());

        Assert.Equal(RyuseiRefusal.None, Check(request));
    }

    [Theory]
    [InlineData(RyuseiPingMode.None)]
    [InlineData(RyuseiPingMode.NewGc)]
    public void Non_attach_modes_refuse_an_attach_gc(RyuseiPingMode mode)
    {
        var request = Request(Agent(_self), [Target(_self, _project)], ping: mode, attachGc: Guid.CreateVersion7());

        Assert.Equal(RyuseiRefusal.PingModeMismatch, Check(request));
    }

    [Fact]
    public void A_kind_that_does_not_match_its_spec_is_refused()
    {
        var request = Request(Agent(_self), [Target(_self, _project)], spec: Timer(), kind: RyuseiTriggerKind.Interval);

        Assert.Equal(RyuseiRefusal.KindMismatch, Check(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_purpose_is_refused(string purpose)
    {
        Assert.Equal(RyuseiRefusal.InvalidPurpose, Check(Request(Agent(_self), [Target(_self, _project)], purpose: purpose)));
    }

    // Active-trigger cap arithmetic.

    [Fact]
    public void The_fiftieth_active_trigger_is_allowed_and_the_fifty_first_is_refused()
    {
        var at49 = new Dictionary<Guid, int> { [_coworker] = 49 };
        var at50 = new Dictionary<Guid, int> { [_coworker] = 50 };

        Assert.True(RyuseiTriggerRules.CheckActiveCaps([_coworker], at49, 50).IsAllowed);

        var refused = RyuseiTriggerRules.CheckActiveCaps([_coworker], at50, 50);
        Assert.Equal(RyuseiRefusal.ActiveTriggerCapReached, refused.Refusal);
        Assert.Contains("50", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_missing_from_the_live_counts_has_none()
    {
        Assert.True(RyuseiTriggerRules.CheckActiveCaps([_coworker], new Dictionary<Guid, int>(), 1).IsAllowed);
    }

    [Fact]
    public void One_full_target_refuses_the_whole_trigger()
    {
        var live = new Dictionary<Guid, int> { [_self] = 0, [_coworker] = 50 };

        var check = RyuseiTriggerRules.CheckActiveCaps([_self, _coworker], live, 50);

        Assert.Equal(RyuseiRefusal.ActiveTriggerCapReached, check.Refusal);
    }

    [Fact]
    public void CheckCreate_does_not_apply_the_active_trigger_cap()
    {
        // The live count is not part of the request, so the cap is left to CheckActiveCaps (the service runs it under the lock).
        Assert.Equal(RyuseiRefusal.None, Check(Request(Agent(_self), [Target(_coworker, _project)]), new RyuseiLimits { MaxTriggersPerAgent = 0 }));
    }
}

/// <summary>Captures audit appends in memory.</summary>
internal sealed class CapturingAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }
}

/// <summary>The service's refusal path needs no database: a refused create never opens a connection, and writes only an audit row.</summary>
public sealed class RyuseiTriggerServiceRefusalTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    // Port 1 on localhost refuses connections. If the refused path ever opened one, these tests would fail with a connection error.
    private static NpgsqlDataSource UnreachableSource() =>
        NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1");

    [Fact]
    public async Task A_refused_create_writes_one_refusal_audit_row_and_opens_no_connection()
    {
        var audit = new CapturingAuditLog();
        var project = Guid.CreateVersion7();
        var creator = Guid.CreateVersion7();
        var service = new RyuseiTriggerService(UnreachableSource(), audit);

        var result = await service.CreateAsync(
            RyuseiActor.ForAgent(creator, project),
            project,
            [new RyuseiTargetInfo(Guid.CreateVersion7(), Guid.CreateVersion7(), IsActive: true)],
            RyuseiTriggerKind.Timer,
            new RyuseiTimerTriggerSpec(_now.AddHours(1)),
            "check the build",
            taskId: null,
            attachGcId: null,
            RyuseiPingMode.None,
            chainDepth: 0,
            now: _now,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RyuseiTriggerStatus.Refused, result.Status);
        Assert.Equal(RyuseiRefusal.TargetOutsideProject, result.Refusal);
        Assert.Null(result.TriggerId);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("ryusei.trigger.refused", entry.Kind);
        Assert.Equal($"agent:{creator:D}", entry.Actor);
        Assert.Contains("\"refusal\":\"TargetOutsideProject\"", entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_coworker_trigger_under_off_is_audited_and_not_stored()
    {
        var audit = new CapturingAuditLog();
        var project = Guid.CreateVersion7();
        var creator = Guid.CreateVersion7();
        var service = new RyuseiTriggerService(UnreachableSource(), audit);

        var result = await service.CreateAsync(
            RyuseiActor.ForAgent(creator, project),
            project,
            [new RyuseiTargetInfo(Guid.CreateVersion7(), project, IsActive: true)],
            RyuseiTriggerKind.Timer,
            new RyuseiTimerTriggerSpec(_now.AddHours(1)),
            "check the build",
            taskId: null,
            attachGcId: null,
            RyuseiPingMode.None,
            chainDepth: 0,
            now: _now,
            coworker: RyuseiCoworkerTriggers.Off,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RyuseiRefusal.CoworkerTriggersOff, result.Refusal);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task An_invalid_predicate_is_refused_before_any_database_access()
    {
        var audit = new CapturingAuditLog();
        var project = Guid.CreateVersion7();
        var service = new RyuseiTriggerService(UnreachableSource(), audit);
        var spec = new RyuseiEventTriggerSpec(new RyuseiPredicate { Kind = "not.a.kind" });

        var result = await service.CreateAsync(
            RyuseiActor.ForOwner(Guid.CreateVersion7()),
            project,
            [new RyuseiTargetInfo(Guid.CreateVersion7(), project, IsActive: true)],
            RyuseiTriggerKind.Event,
            spec,
            "wake on merge",
            taskId: null,
            attachGcId: null,
            RyuseiPingMode.None,
            chainDepth: 0,
            now: _now,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RyuseiRefusal.InvalidPredicate, result.Refusal);
        Assert.Contains("\"refusal\":\"InvalidPredicate\"", Assert.Single(audit.Entries).Payload, StringComparison.Ordinal);
    }
}
