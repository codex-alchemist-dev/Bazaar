using Bazaar.Server.AgentSafety.Anomaly;
using Bazaar.Server.AgentSafety.AutoContainment;
using Bazaar.Server.Security.KillSwitch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AgentSafety.Tests;

/// <summary>One agent is one agent however its id is spelled.</summary>
public sealed class AgentIdCanonicalisationTests
{
    private static readonly Guid _agent = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    private static readonly WindowedRuleOptions _threeInAMinute = new(3, TimeSpan.FromMinutes(1), AnomalySeverity.High);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero));

    private static string[] Spellings =>
    [
        _agent.ToString("D"),
        _agent.ToString("D").ToUpperInvariant(),
        _agent.ToString("B"),
        _agent.ToString("N"),
        "  " + _agent.ToString("D") + " ",
    ];

    [Fact]
    public void Canonical_folds_every_guid_spelling_and_only_trims_other_ids()
    {
        Assert.All(Spellings, s => Assert.Equal(_agent.ToString("D"), AgentIds.Canonical(s)));
        Assert.Equal("agent-a", AgentIds.Canonical(" agent-a "));
        Assert.Equal("Agent-A", AgentIds.Canonical("Agent-A"));
    }

    [Fact]
    public void Read_burst_counts_all_spellings_of_one_agent_together()
    {
        var rule = new ReadBurstRule(_threeInAMinute, _clock);
        var now = _clock.GetUtcNow();

        Assert.Empty(rule.Evaluate(Read(Spellings[0], now)));
        Assert.Empty(rule.Evaluate(Read(Spellings[1], now)));
        var alert = Assert.Single(rule.Evaluate(Read(Spellings[2], now)));

        Assert.Equal(_agent.ToString("D"), alert.AgentId);
    }

    [Fact]
    public void Approval_flood_counts_all_spellings_of_one_agent_together()
    {
        var rule = new ApprovalFloodRule(_threeInAMinute, _clock);
        var now = _clock.GetUtcNow();

        Assert.Empty(rule.Evaluate(Approval(Spellings[1], now)));
        Assert.Empty(rule.Evaluate(Approval(Spellings[3], now)));
        Assert.Single(rule.Evaluate(Approval(Spellings[2], now)));
    }

    [Fact]
    public async Task The_engine_hands_rules_and_alerts_the_canonical_id()
    {
        var sink = new ChannelAnomalyAlertSource();
        var engine = new AnomalyEngine([new CanaryHitRule(new SeverityRuleOptions(AnomalySeverity.Critical))], sink, NullLogger<AnomalyEngine>.Instance, time: _clock);

        await engine.HandleAsync(new AnomalyEvent(AnomalyEventKind.CanaryTouched, _agent.ToString("B"), null, "canary", true, _clock.GetUtcNow()), TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await foreach (var alert in sink.ReadAllAsync(cts.Token))
        {
            Assert.Equal(_agent.ToString("D"), alert.AgentId);
            break;
        }
    }

    [Fact]
    public async Task An_alert_for_a_respelled_agent_is_deduplicated_against_its_containment()
    {
        var containment = new FakeContainment();
        var contained = new InMemoryContainmentStateStore();
        var records = new InMemoryContainmentRecordStore();
        var service = new AutoContainmentService(
            new ListAlertSource(), containment, contained, records, new ListAuditLog(), new RecordingNoticeSink(),
            Options.Create(new AutoContainmentOptions()), _clock, NullLogger<AutoContainmentService>.Instance);
        var ct = TestContext.Current.CancellationToken;

        await service.HandleAlertAsync(Alert(Spellings[1]), ct);
        await contained.AddAsync(new ContainedAgent(_agent.ToString("D"), _clock.GetUtcNow(), "anomaly"), ct);
        await service.HandleAlertAsync(Alert(Spellings[2]), ct);
        await service.HandleAlertAsync(Alert(Spellings[3]), ct);

        var once = Assert.Single(containment.Contained);
        Assert.Equal(_agent.ToString("D"), once.AgentId);
        Assert.Single(records.Records);
    }

    [Fact]
    public async Task The_release_guard_treats_spellings_as_one_agent()
    {
        var guard = new ContainmentReleaseGuard();
        using var lease = await guard.AcquireAsync(Spellings[1], TestContext.Current.CancellationToken);

        Assert.Null(guard.TryAcquire(Spellings[2]));
    }

    private AnomalyAlert Alert(string agent) => new("canary_hit", agent, null, AnomalySeverity.High, _clock.GetUtcNow(), "reason");

    private static AnomalyEvent Read(string agent, DateTimeOffset at) => new(AnomalyEventKind.ObjectRead, agent, null, "contacts", true, at);

    private static AnomalyEvent Approval(string agent, DateTimeOffset at) => new(AnomalyEventKind.ApprovalRequested, agent, null, null, true, at);
}
