using System.Text.Json.Nodes;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The fire audit payload and kind (doc 38, Authority, audit and taint) as a pure function. No database: the Postgres-backed
/// write goes through <see cref="RyuseiFireWriter"/> and is covered by the database tests.
/// </summary>
public sealed class RyuseiFireAuditTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid _fire = Guid.Parse("f1f1f1f1-f1f1-7f1f-8f1f-f1f1f1f1f1f1");
    private static readonly Guid _trigger = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _project = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid _creator = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly Guid _target = Guid.Parse("44444444-4444-7444-8444-444444444444");
    private const string _secretPurpose = "watch the release-candidate tag for the customer escalation";

    private static RyuseiTriggerRecord Trigger() => new(
        TriggerId: _trigger,
        ProjectId: _project,
        CreatedBy: _creator,
        Kind: RyuseiTriggerKind.Timer,
        TargetAgentIds: [_target],
        Purpose: _secretPurpose,
        SpecJson: "{}",
        TaskId: null,
        AttachGcId: null,
        PingMode: RyuseiPingMode.None,
        State: RyuseiTriggerState.Active,
        NextFireAt: _now,
        FireCount: 0,
        ConsecutiveRefusals: 0,
        MaxFires: null,
        ExpiresAt: null,
        ChainDepth: 0,
        Version: 1,
        CreatedAt: _now,
        UpdatedAt: _now);

    private static RyuseiFireDecision Woke() => new(
        RyuseiFireOutcome.Woke,
        ScheduledFor: _now,
        NextState: RyuseiTriggerState.Active,
        NextFireAt: null,
        ConsecutiveRefusals: 0,
        WakeAgentIds: [_target],
        WakeReason: "ryusei_timer",
        Cause: new JsonObject { ["wakeReason"] = "ryusei_timer", ["missedCount"] = 0 });

    private static RyuseiFireDecision Refused() => new(
        RyuseiFireOutcome.RefusedCooldown,
        ScheduledFor: _now,
        NextState: RyuseiTriggerState.Active,
        NextFireAt: _now.AddMinutes(1),
        ConsecutiveRefusals: 1,
        WakeAgentIds: [],
        WakeReason: null,
        Cause: new JsonObject { ["refusal"] = "CooldownActive", ["consecutiveRefusals"] = 1 });

    [Fact]
    public void Woke_fire_is_the_fired_kind()
    {
        var entry = RyuseiFireAudit.Build(_fire, Trigger(), Woke(), _now);

        Assert.Equal(RyuseiFireAudit.FiredKind, entry.Kind);
        Assert.Equal("ryusei.trigger.fired", entry.Kind);
    }

    [Fact]
    public void Refused_fire_is_the_fire_refused_kind()
    {
        var entry = RyuseiFireAudit.Build(_fire, Trigger(), Refused(), _now);

        Assert.Equal(RyuseiFireAudit.FireRefusedKind, entry.Kind);
        Assert.Equal("ryusei.trigger.fire_refused", entry.Kind);
    }

    [Fact]
    public void Kind_follows_the_outcome_for_every_outcome()
    {
        Assert.Equal("ryusei.trigger.fired", RyuseiFireAudit.KindFor(RyuseiFireOutcome.Woke));
        Assert.Equal("ryusei.trigger.fire_refused", RyuseiFireAudit.KindFor(RyuseiFireOutcome.RefusedCap));
        Assert.Equal("ryusei.trigger.fire_refused", RyuseiFireAudit.KindFor(RyuseiFireOutcome.RefusedCooldown));
        Assert.Equal("ryusei.trigger.fire_refused", RyuseiFireAudit.KindFor(RyuseiFireOutcome.SkippedResting));
        Assert.Equal("ryusei.trigger.fire_refused", RyuseiFireAudit.KindFor(RyuseiFireOutcome.Error));
    }

    [Fact]
    public void Payload_never_carries_the_purpose_text()
    {
        var entry = RyuseiFireAudit.Build(_fire, Trigger(), Woke(), _now);
        var refused = RyuseiFireAudit.Build(_fire, Trigger(), Refused(), _now);

        Assert.DoesNotContain("release-candidate", entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("customer escalation", entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("release-candidate", refused.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("customer escalation", refused.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_carries_ids_outcome_and_cause()
    {
        var payload = JsonNode.Parse(RyuseiFireAudit.Build(_fire, Trigger(), Refused(), _now).Payload)!.AsObject();

        Assert.Equal(_fire.ToString("D"), payload["fireId"]!.GetValue<string>());
        Assert.Equal(_trigger.ToString("D"), payload["triggerId"]!.GetValue<string>());
        Assert.Equal(_project.ToString("D"), payload["projectId"]!.GetValue<string>());
        Assert.Equal("refused_cooldown", payload["outcome"]!.GetValue<string>());
        Assert.Equal("CooldownActive", payload["cause"]!["refusal"]!.GetValue<string>());
    }

    [Fact]
    public void Woke_payload_lists_the_woken_agents()
    {
        var payload = JsonNode.Parse(RyuseiFireAudit.Build(_fire, Trigger(), Woke(), _now).Payload)!.AsObject();

        Assert.Equal("woke", payload["outcome"]!.GetValue<string>());
        Assert.Equal(_target.ToString("D"), payload["wokenAgents"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Actor_is_the_system_and_the_creator_is_recorded_as_an_id()
    {
        var entry = RyuseiFireAudit.Build(_fire, Trigger(), Woke(), _now);
        var payload = JsonNode.Parse(entry.Payload)!.AsObject();

        Assert.Equal(RyuseiFireAudit.SystemActor, entry.Actor);
        Assert.Equal(_creator.ToString("D"), payload["createdBy"]!.GetValue<string>());
        Assert.Equal(_now, entry.Ts);
    }

    [Fact]
    public void An_expiry_without_a_fire_is_audited_as_expired_with_its_reason_and_no_purpose_text()
    {
        var entry = RyuseiFireAudit.BuildExpiry(Trigger(), "watcher_past_expiry", _now);
        var payload = JsonNode.Parse(entry.Payload)!.AsObject();

        Assert.Equal("ryusei.trigger.expired", RyuseiFireAudit.ExpiredKind);
        Assert.Equal(RyuseiFireAudit.ExpiredKind, entry.Kind);
        Assert.Equal(RyuseiFireAudit.SystemActor, entry.Actor);
        Assert.Equal(_now, entry.Ts);
        Assert.Equal(_trigger.ToString("D"), payload["triggerId"]!.GetValue<string>());
        Assert.Equal(_project.ToString("D"), payload["projectId"]!.GetValue<string>());
        Assert.Equal(_creator.ToString("D"), payload["createdBy"]!.GetValue<string>());
        Assert.Equal("watcher_past_expiry", payload["reason"]!.GetValue<string>());
        Assert.DoesNotContain("release-candidate", entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("customer escalation", entry.Payload, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => RyuseiFireAudit.BuildExpiry(Trigger(), "", _now));
    }
}
