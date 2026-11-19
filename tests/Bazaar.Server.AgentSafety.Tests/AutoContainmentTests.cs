using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.Approvals;
using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Server.AgentSafety.AutoContainment;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AgentSafety.Tests;

internal sealed class FakeContainment : IAgentContainment, IAgentStop
{
    public List<string> Reasons { get; } = [];

    public List<(string AgentId, string ActorId)> Contained { get; } = [];
    public int ContainCalls { get; private set; }
    public int ReleaseCalls { get; private set; }
    public int FailFirst { get; set; }
    public ContainmentReleaseOutcome ReleaseOutcome { get; set; } = ContainmentReleaseOutcome.Released;

    public Task ContainAsync(string agentId, string actorId, CancellationToken ct = default)
    {
        ContainCalls++;
        if (ContainCalls <= FailFirst)
        {
            throw new InvalidOperationException("contain failed");
        }
        Contained.Add((agentId, actorId));
        return Task.CompletedTask;
    }

    public Task EngageAsync(string agentId, string actorId, string reason, CancellationToken ct = default)
    {
        Reasons.Add(reason);
        return ContainAsync(agentId, actorId, ct);
    }

    public Task<ContainmentReleaseResult> ResumeAsync(string agentId, string reason, ContainmentReleaseRequest request, CancellationToken ct = default) =>
        ReleaseAsync(agentId, request, ct);

    public Task<ReleaseChallenge?> IssueReleaseChallengeAsync(string agentId, string deviceId, CancellationToken ct = default) =>
        Task.FromResult<ReleaseChallenge?>(null);

    public Task<ContainmentReleaseResult> ReleaseAsync(string agentId, ContainmentReleaseRequest request, CancellationToken ct = default)
    {
        ReleaseCalls++;
        return Task.FromResult(new ContainmentReleaseResult(ReleaseOutcome));
    }
}

internal sealed class NullKillSwitchAuditSink : IKillSwitchAuditSink
{
    public Task RecordAsync(KillSwitchAuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>A record store that can be switched to throw, over the in-memory one.</summary>
internal sealed class FlakyRecordStore(InMemoryContainmentRecordStore inner) : IContainmentRecordStore
{
    public bool Down { get; set; }

    public Task<bool> TryAddAsync(ContainmentRecord record, CancellationToken ct = default) => Down ? throw new InvalidOperationException("db down") : inner.TryAddAsync(record, ct);

    public Task<ContainmentRecord?> GetLatestAsync(string agentId, CancellationToken ct = default) => Down ? throw new InvalidOperationException("db down") : inner.GetLatestAsync(agentId, ct);

    public Task<bool> TryTransitionAsync(Guid id, ContainmentRecordState expected, ContainmentRecordState target, CancellationToken ct = default) =>
        Down ? throw new InvalidOperationException("db down") : inner.TryTransitionAsync(id, expected, target, ct);

    public Task<int> ExpireDueAsync(DateTimeOffset now, CancellationToken ct = default) => Down ? throw new InvalidOperationException("db down") : inner.ExpireDueAsync(now, ct);
}

internal sealed class ListAuditLog : IAuditLog
{
    private readonly List<AuditEntry> _entries = [];

    public IReadOnlyList<AuditEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToArray();
            }
        }
    }

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        lock (_entries)
        {
            _entries.Add(e);
        }
        return Task.CompletedTask;
    }
}

internal sealed class RecordingNoticeSink : INoticeSink
{
    public List<ContainmentNotice> Notices { get; } = [];
    public bool Fail { get; set; }

    public ValueTask PublishAsync(ContainmentNotice notice, CancellationToken ct)
    {
        if (Fail)
        {
            throw new InvalidOperationException("notice transport down");
        }
        Notices.Add(notice);
        return ValueTask.CompletedTask;
    }
}

internal sealed class ListAlertSource(params AnomalyAlert[] alerts) : IAnomalyAlertSource
{
    public async IAsyncEnumerable<AnomalyAlert> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var alert in alerts)
        {
            yield return alert;
        }
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
    }
}

internal sealed class NoopPauser : IVmPauser
{
    public List<string> Resumed { get; } = [];
    public Task PauseAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;

    public Task ResumeAsync(string agentId, CancellationToken ct = default)
    {
        Resumed.Add(agentId);
        return Task.CompletedTask;
    }
}

internal sealed class NoopRevoker : IAgentTokenRevoker
{
    public Task RevokeAsync(string agentId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default) => Task.FromResult(TokenResumeResult.NotReversible);
}

public sealed class AutoContainmentTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeContainment _containment = new();
    private readonly InMemoryContainmentStateStore _contained = new();
    private readonly InMemoryContainmentRecordStore _records = new();
    private readonly ListAuditLog _audit = new();
    private readonly RecordingNoticeSink _notices = new();
    private readonly AutoContainmentOptions _options = new();

    private AutoContainmentService Service() =>
        new(new ListAlertSource(), _containment, _contained, _records, _audit, _notices, Options.Create(_options), _clock, NullLogger<AutoContainmentService>.Instance);

    private AnomalyAlert Alert(string agent = "agent-a", AnomalySeverity severity = AnomalySeverity.High, string rule = "canary_hit") =>
        new(rule, agent, null, severity, _clock.GetUtcNow(), "reason");

    /// <summary>Advances the fake clock until a task that waits on it finishes.</summary>
    private async Task CompleteAsync(Task task)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await task;
    }

    [Fact]
    public async Task An_alert_at_the_threshold_contains_that_one_agent_with_the_breach_actor()
    {
        await Service().HandleAlertAsync(Alert(), TestContext.Current.CancellationToken);

        var contained = Assert.Single(_containment.Contained);
        Assert.Equal(("agent-a", "anomaly:canary_hit"), contained);
        var record = Assert.Single(_records.Records);
        Assert.Equal(ContainmentRecordState.InForce, record.State);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(15), record.UndoUntil);
        Assert.Contains(_notices.Notices, n => n.Kind == ContainmentNoticeKind.Contained && n.AgentId == "agent-a");
    }

    [Theory]
    [InlineData(AnomalySeverity.Low)]
    [InlineData(AnomalySeverity.Medium)]
    public async Task An_alert_below_the_threshold_is_only_recorded(AnomalySeverity severity)
    {
        await Service().HandleAlertAsync(Alert(severity: severity), TestContext.Current.CancellationToken);

        Assert.Equal(0, _containment.ContainCalls);
        Assert.Empty(_records.Records);
    }

    [Fact]
    public async Task A_lowered_threshold_contains_on_a_lower_severity()
    {
        _options.Threshold = AnomalySeverity.Medium;

        await Service().HandleAlertAsync(Alert(severity: AnomalySeverity.Medium), TestContext.Current.CancellationToken);

        Assert.Equal(1, _containment.ContainCalls);
    }

    [Fact]
    public async Task A_burst_of_alerts_for_one_agent_yields_one_containment()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        // The adapter records the containment in its store; mirror that so the second alert sees an agent already contained.
        var first = Alert();
        await service.HandleAlertAsync(first, ct);
        await _contained.AddAsync(new ContainedAgent("agent-a", _clock.GetUtcNow(), "anomaly:canary_hit"), ct);
        await service.HandleAlertAsync(Alert(rule: "read_burst"), ct);
        await service.HandleAlertAsync(Alert(severity: AnomalySeverity.Critical), ct);

        Assert.Equal(1, _containment.ContainCalls);
        Assert.Single(_records.Records);
    }

    [Fact]
    public async Task Containment_is_per_agent_only()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.HandleAlertAsync(Alert("agent-a"), ct);
        await service.HandleAlertAsync(Alert("agent-b"), ct);

        Assert.Equal(["agent-a", "agent-b"], _containment.Contained.Select(c => c.AgentId).ToArray());
        Assert.All(_records.Records, r => Assert.NotEqual("*", r.AgentId));
    }

    [Fact]
    public async Task A_malformed_alert_contains_nothing()
    {
        var service = Service();
        await service.HandleAlertAsync(Alert(agent: " "), TestContext.Current.CancellationToken);
        await service.HandleAlertAsync(Alert(rule: ""), TestContext.Current.CancellationToken);

        Assert.Equal(0, _containment.ContainCalls);
    }

    [Fact]
    public async Task The_service_reads_alerts_from_the_source_and_contains()
    {
        var source = new ListAlertSource(Alert("agent-x"), Alert("agent-y", AnomalySeverity.Low));
        var service = new AutoContainmentService(
            source, _containment, _contained, _records, _audit, _notices, Options.Create(_options), _clock, NullLogger<AutoContainmentService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);

        for (var i = 0; i < 200 && _containment.Contained.Count == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal("agent-x", Assert.Single(_containment.Contained).AgentId);
    }

    [Fact]
    public async Task Containment_is_audited_without_alert_detail()
    {
        await Service().HandleAlertAsync(Alert(), TestContext.Current.CancellationToken);

        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("containment.auto", entry.Kind);
        Assert.Equal("anomaly:canary_hit", entry.Actor);
        var payload = JsonNode.Parse(entry.Payload)!;
        Assert.Equal("contained", (string?)payload["step"]);
        Assert.DoesNotContain("reason", entry.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_notice_does_not_block_containment()
    {
        _notices.Fail = true;

        await Service().HandleAlertAsync(Alert(), TestContext.Current.CancellationToken);

        Assert.Equal(1, _containment.ContainCalls);
        Assert.Equal(ContainmentRecordState.InForce, Assert.Single(_records.Records).State);
    }

    [Fact]
    public async Task A_failing_contain_is_retried_and_succeeds()
    {
        _containment.FailFirst = 2;

        await CompleteAsync(Service().HandleAlertAsync(Alert(), TestContext.Current.CancellationToken));

        Assert.Equal(3, _containment.ContainCalls);
        Assert.Equal(ContainmentRecordState.InForce, Assert.Single(_records.Records).State);
    }

    [Fact]
    public async Task A_contain_that_never_completes_is_recorded_as_failed_and_never_in_force()
    {
        _containment.FailFirst = int.MaxValue;

        await CompleteAsync(Service().HandleAlertAsync(Alert(), TestContext.Current.CancellationToken));

        Assert.Equal(_options.MaxContainAttempts, _containment.ContainCalls);
        var record = Assert.Single(_records.Records);
        Assert.Equal(ContainmentRecordState.Failed, record.State);
        Assert.Contains(_notices.Notices, n => n.Kind == ContainmentNoticeKind.ContainmentFailed);
    }

    [Fact]
    public async Task A_failed_containment_is_tried_again_by_the_next_alert_even_if_partly_contained()
    {
        var ct = TestContext.Current.CancellationToken;
        _containment.FailFirst = _options.MaxContainAttempts;
        var service = Service();
        await CompleteAsync(service.HandleAlertAsync(Alert(), ct));
        await _contained.AddAsync(new ContainedAgent("agent-a", _clock.GetUtcNow(), "x"), ct); // adapter marks before it pauses

        await service.HandleAlertAsync(Alert(), ct);

        Assert.Equal(ContainmentRecordState.InForce, _records.Records[^1].State);
    }

    [Fact]
    public async Task A_record_whose_agent_was_released_by_the_normal_path_does_not_block_a_new_containment()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service();
        await service.HandleAlertAsync(Alert(), ct);
        // The contained store never saw the agent, as if the owner released it by the normal path.
        _clock.Advance(TimeSpan.FromMinutes(1));

        await service.HandleAlertAsync(Alert(), ct);

        Assert.Equal(2, _containment.ContainCalls);
        Assert.Equal([ContainmentRecordState.Released, ContainmentRecordState.InForce], _records.Records.Select(r => r.State).ToArray());
    }

    [Fact]
    public async Task Elapsed_windows_are_marked_expired()
    {
        await Service().HandleAlertAsync(Alert(), TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromMinutes(16));

        var count = await _records.ExpireDueAsync(_clock.GetUtcNow(), TestContext.Current.CancellationToken);

        Assert.Equal(1, count);
        Assert.Equal(ContainmentRecordState.WindowExpired, _records.Records[0].State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    public void An_undo_window_below_the_floor_is_rejected(int seconds)
    {
        var options = new AutoContainmentOptions { UndoWindow = TimeSpan.FromSeconds(seconds) };

        Assert.NotNull(options.Validate());
        Assert.True(new AutoContainmentOptionsValidator().Validate(null, options).Failed);
        Assert.Throws<ArgumentException>(() =>
            new AutoContainmentService(new ListAlertSource(), _containment, _contained, _records, _audit, _notices, Options.Create(options), _clock, NullLogger<AutoContainmentService>.Instance));
    }

    [Fact]
    public void The_defaults_are_valid_and_the_floor_itself_is_accepted()
    {
        Assert.Null(new AutoContainmentOptions().Validate());
        Assert.Null(new AutoContainmentOptions { UndoWindow = TimeSpan.FromMinutes(1) }.Validate());
        Assert.Equal(AnomalySeverity.High, new AutoContainmentOptions().Threshold);
        Assert.Equal(TimeSpan.FromMinutes(15), new AutoContainmentOptions().UndoWindow);
    }

    [Fact]
    public void Registration_adds_the_service_once_and_keeps_an_earlier_notice_sink()
    {
        var sink = new RecordingNoticeSink();
        var services = new ServiceCollection();
        services.AddSingleton<INoticeSink>(sink);

        services.AddBazaarAutoContainment();
        services.AddBazaarAutoContainment();

        Assert.Single(services, d => d.ServiceType == typeof(AutoContainmentService));
        Assert.Single(services, d => d.ServiceType == typeof(INoticeSink));
    }

    // ---- undo ----

    private sealed class UndoRig
    {
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));
        public ECDsa OwnerKey { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public InMemoryContainmentStateStore Contained { get; } = new();
        public InMemoryContainmentRecordStore Records { get; } = new();
        public ListAuditLog Audit { get; } = new();
        public RecordingNoticeSink Notices { get; } = new();
        public NoopPauser Pauser { get; } = new();
        public AgentContainmentAdapter Adapter { get; }
        public KillSwitchEngine Engine { get; }
        public AutoContainmentService Service { get; }
        public AutoContainmentUndo Undo { get; }

        public UndoRig()
        {
            var owners = new InMemoryOwnerKeyRegistry();
            owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", OwnerKey.ExportSubjectPublicKeyInfo()));
            Adapter = new AgentContainmentAdapter(Pauser, new NoopRevoker(), owners, Audit, new InMemoryNonceStore(Clock), Contained, Clock);
            Engine = new KillSwitchEngine(new NullKillSwitchAuditSink(), Adapter, Clock);
            var stop = new KillSwitchAgentStop(Engine);
            Service = new AutoContainmentService(
                new ListAlertSource(), stop, Contained, Records, Audit, Notices, Options.Create(new AutoContainmentOptions()), Clock, NullLogger<AutoContainmentService>.Instance);
            Undo = new AutoContainmentUndo(stop, Records, Audit, Notices, Clock, NullLogger<AutoContainmentUndo>.Instance);
        }

        public async Task<ContainmentReleaseRequest> SignedAsync(ECDsa? key = null, CancellationToken ct = default)
        {
            var challenge = await Adapter.IssueReleaseChallengeAsync("agent-a", "dev-owner", ct) ?? throw new InvalidOperationException("no challenge");
            var signature = (key ?? OwnerKey).SignData(
                ApprovalSignature.ReleaseMessage("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new ContainmentReleaseRequest("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt, signature);
        }
    }

    private static AnomalyAlert HighAlert(DateTimeOffset at) => new("canary_hit", "agent-a", null, AnomalySeverity.High, at, "reason");

    [Fact]
    public async Task An_unreadable_record_store_does_not_let_the_alert_pass()
    {
        var flaky = new FlakyRecordStore(_records) { Down = true };
        var service = new AutoContainmentService(
            new ListAlertSource(), _containment, _contained, flaky, _audit, _notices, Options.Create(_options), _clock, NullLogger<AutoContainmentService>.Instance);

        await service.HandleAlertAsync(Alert(), TestContext.Current.CancellationToken);

        // Fail closed: the agent is contained, and the owner is told, although no record could be read or written.
        Assert.Equal(("agent-a", "anomaly:canary_hit"), Assert.Single(_containment.Contained));
        var notice = Assert.Single(_notices.Notices);
        Assert.Equal(ContainmentNoticeKind.Contained, notice.Kind);
        Assert.Null(notice.UndoUntil);
    }

    [Fact]
    public async Task A_failed_containment_still_reaches_the_owner_when_the_record_store_is_down()
    {
        _options.MaxContainAttempts = 1;
        _containment.FailFirst = int.MaxValue;
        var flaky = new FlakyRecordStore(_records) { Down = true };
        var service = new AutoContainmentService(
            new ListAlertSource(), _containment, _contained, flaky, _audit, _notices, Options.Create(_options), _clock, NullLogger<AutoContainmentService>.Instance);

        await service.HandleAlertAsync(Alert(), TestContext.Current.CancellationToken);

        Assert.Equal(ContainmentNoticeKind.ContainmentFailed, Assert.Single(_notices.Notices).Kind);
        Assert.Contains(_audit.Entries, e => e.Payload.Contains("contain_failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Automatic_containment_engages_the_kill_switch_stop_and_undo_lifts_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new UndoRig();
        Assert.True(rig.Engine.Evaluate("agent-a", ToolEffect.Write).Allowed);

        await rig.Service.HandleAlertAsync(HighAlert(rig.Clock.GetUtcNow()), ct);

        // The stop is what denies the agent's tool calls; containment alone (leases, VMs) would leave them allowed.
        foreach (var effect in Enum.GetValues<ToolEffect>())
        {
            Assert.False(rig.Engine.Evaluate("agent-a", effect).Allowed);
        }
        Assert.True(rig.Engine.Evaluate("agent-b", ToolEffect.Write).Allowed);
        Assert.Equal("anomaly:canary_hit", Assert.Single(rig.Engine.GetStatus().AgentStops).ActorId);

        var undone = await rig.Undo.UndoAsync("agent-a", await rig.SignedAsync(ct: ct), ct);

        Assert.Equal(AutoContainmentUndoOutcome.Undone, undone.Outcome);
        Assert.True(rig.Engine.Evaluate("agent-a", ToolEffect.Write).Allowed);
    }

    [Fact]
    public async Task A_refused_undo_leaves_the_kill_switch_stop_in_place()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new UndoRig();
        await rig.Service.HandleAlertAsync(HighAlert(rig.Clock.GetUtcNow()), ct);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var result = await rig.Undo.UndoAsync("agent-a", await rig.SignedAsync(attacker, ct), ct);

        Assert.Equal(AutoContainmentUndoOutcome.ReleaseRefused, result.Outcome);
        Assert.False(rig.Engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
        Assert.Single(rig.Engine.GetStatus().AgentStops);
    }

    [Fact]
    public async Task Undo_inside_the_window_with_a_signed_release_restores_and_is_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new UndoRig();
        await rig.Service.HandleAlertAsync(HighAlert(rig.Clock.GetUtcNow()), ct);
        rig.Clock.Advance(TimeSpan.FromMinutes(5));

        var result = await rig.Undo.UndoAsync("agent-a", await rig.SignedAsync(ct: ct), ct);

        Assert.Equal(AutoContainmentUndoOutcome.Undone, result.Outcome);
        Assert.Equal(ContainmentRecordState.Undone, Assert.Single(rig.Records.Records).State);
        Assert.Equal(["agent-a"], rig.Pauser.Resumed);
        Assert.Empty(await rig.Contained.LoadAsync(ct));
        Assert.Contains(rig.Audit.Entries, e => e.Kind == "containment.auto" && e.Payload.Contains("\"undone\"", StringComparison.Ordinal));
        Assert.Contains(rig.Audit.Entries, e => e.Kind == AgentContainmentAdapter.ReleaseKind);
        Assert.Contains(rig.Notices.Notices, n => n.Kind == ContainmentNoticeKind.Undone);
    }

    [Fact]
    public async Task Undo_without_a_valid_owner_signature_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new UndoRig();
        await rig.Service.HandleAlertAsync(HighAlert(rig.Clock.GetUtcNow()), ct);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var result = await rig.Undo.UndoAsync("agent-a", await rig.SignedAsync(attacker, ct), ct);

        Assert.Equal(AutoContainmentUndoOutcome.ReleaseRefused, result.Outcome);
        Assert.Equal(ContainmentReleaseOutcome.Denied, result.Release?.Outcome);
        Assert.Equal(ContainmentRecordState.InForce, Assert.Single(rig.Records.Records).State);
        Assert.Empty(rig.Pauser.Resumed);
        Assert.Single(await rig.Contained.LoadAsync(ct));
    }

    [Fact]
    public async Task A_replayed_signed_release_cannot_undo_twice()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new UndoRig();
        await rig.Service.HandleAlertAsync(HighAlert(rig.Clock.GetUtcNow()), ct);
        var request = await rig.SignedAsync(ct: ct);

        await rig.Undo.UndoAsync("agent-a", request, ct);
        var again = await rig.Undo.UndoAsync("agent-a", request, ct);

        Assert.Equal(AutoContainmentUndoOutcome.NotInForce, again.Outcome);
        Assert.Single(rig.Pauser.Resumed);
    }

    [Fact]
    public async Task Undo_after_the_window_is_refused_without_calling_release()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = new UndoRig();
        await rig.Service.HandleAlertAsync(HighAlert(rig.Clock.GetUtcNow()), ct);
        var request = await rig.SignedAsync(ct: ct);
        rig.Clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        var request2 = request; // signed earlier; the window, not the signature, is what refuses

        var result = await rig.Undo.UndoAsync("agent-a", request2, ct);

        Assert.Equal(AutoContainmentUndoOutcome.WindowExpired, result.Outcome);
        Assert.Equal(ContainmentRecordState.WindowExpired, Assert.Single(rig.Records.Records).State);
        Assert.Empty(rig.Pauser.Resumed);
        Assert.Single(await rig.Contained.LoadAsync(ct));
        Assert.Contains(rig.Audit.Entries, e => e.Kind == "containment.auto" && e.Payload.Contains("undo_refused_window_expired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undo_is_refused_when_the_release_is_not_Released_and_the_record_stays_in_force()
    {
        var ct = TestContext.Current.CancellationToken;
        var containment = new FakeContainment { ReleaseOutcome = ContainmentReleaseOutcome.NotContained };
        var records = new InMemoryContainmentRecordStore();
        var audit = new ListAuditLog();
        var now = _clock.GetUtcNow();
        await records.TryAddAsync(new ContainmentRecord(Guid.NewGuid(), "agent-a", "canary_hit", AnomalySeverity.High, now, now.AddMinutes(15), ContainmentRecordState.InForce), ct);
        var undo = new AutoContainmentUndo(containment, records, audit, _notices, _clock, NullLogger<AutoContainmentUndo>.Instance);

        var result = await undo.UndoAsync("agent-a", new ContainmentReleaseRequest("agent-a", "d", new byte[16], now, new byte[64]), ct);

        Assert.Equal(AutoContainmentUndoOutcome.ReleaseRefused, result.Outcome);
        Assert.Equal(ContainmentRecordState.InForce, records.Records[0].State);
    }

    [Fact]
    public async Task Undo_with_no_record_is_refused_and_audited()
    {
        var undo = new AutoContainmentUndo(_containment, _records, _audit, _notices, _clock, NullLogger<AutoContainmentUndo>.Instance);

        var result = await undo.UndoAsync("agent-a", new ContainmentReleaseRequest("agent-a", "d", new byte[16], _clock.GetUtcNow(), new byte[64]), TestContext.Current.CancellationToken);

        Assert.Equal(AutoContainmentUndoOutcome.NotInForce, result.Outcome);
        Assert.Equal(0, _containment.ReleaseCalls);
        Assert.Single(_audit.Entries);
    }
}
