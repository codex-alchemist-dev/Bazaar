using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Inbox;
using Bazaar.Server.Gc.Write;
using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The attach ping line (doc 38, WP-R7) as a pure builder: which GC it goes to, that it is a system line, that it carries ids
/// and never purpose text, and that a retried fire keys to the same client message id. The inbox rank for the new ping kind is
/// checked here too.
/// </summary>
public sealed class RyuseiPingLineTests
{
    private static readonly DateTimeOffset _fireAt = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid _trigger = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _gc = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid _agentA = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _agentB = Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb");

    private static RyuseiTriggerRecord Trigger(RyuseiPingMode mode, Guid? attachGc, string purpose = "ship the weekly digest") => new(
        TriggerId: _trigger,
        ProjectId: Guid.Parse("33333333-3333-7333-8333-333333333333"),
        CreatedBy: _agentA,
        Kind: RyuseiTriggerKind.Timer,
        TargetAgentIds: [_agentA, _agentB],
        Purpose: purpose,
        SpecJson: "{}",
        TaskId: null,
        AttachGcId: attachGc,
        PingMode: mode,
        State: RyuseiTriggerState.Active,
        NextFireAt: _fireAt,
        FireCount: 0,
        ConsecutiveRefusals: 0,
        MaxFires: null,
        ExpiresAt: null,
        ChainDepth: 0,
        Version: 1,
        CreatedAt: _fireAt,
        UpdatedAt: _fireAt);

    [Fact]
    public void Attach_line_is_a_system_line_in_the_attach_gc()
    {
        var request = RyuseiPingLine.Build(Trigger(RyuseiPingMode.Attach, _gc), _gc, _fireAt, [_agentA, _agentB]);

        Assert.Equal(_gc, request.GcId);
        Assert.Equal(GcEventKinds.System, request.Kind);
        Assert.Equal(AuthorKind.System, request.AuthorKind);
        Assert.Equal(Guid.Empty, request.AuthorId);
        Assert.Equal(SourceTrust.System, request.SourceTrust);
        Assert.Null(request.Message);
    }

    [Fact]
    public void Attach_line_names_the_trigger_and_the_targets_by_id()
    {
        var request = RyuseiPingLine.Build(Trigger(RyuseiPingMode.Attach, _gc), _gc, _fireAt, [_agentA, _agentB]);

        Assert.NotNull(request.PayloadJson);
        Assert.Contains(RyuseiPingLine.PayloadKind, request.PayloadJson);
        Assert.Contains(_trigger.ToString("D"), request.PayloadJson);
        Assert.Contains(_agentA.ToString("D"), request.PayloadJson);
        Assert.Contains(_agentB.ToString("D"), request.PayloadJson);
    }

    [Fact]
    public void Attach_line_never_carries_the_purpose_text()
    {
        // Purpose is agent-written; the line is read by every member, so it stays out of the payload.
        var request = RyuseiPingLine.Build(Trigger(RyuseiPingMode.Attach, _gc, "PURPOSE-SENTINEL-7731"), _gc, _fireAt, [_agentA]);

        Assert.DoesNotContain("PURPOSE-SENTINEL-7731", request.PayloadJson);
    }

    [Fact]
    public void Retried_fire_keys_to_the_same_client_message_id()
    {
        var first = RyuseiPingLine.Build(Trigger(RyuseiPingMode.Attach, _gc), _gc, _fireAt, [_agentA]);
        var retry = RyuseiPingLine.Build(Trigger(RyuseiPingMode.Attach, _gc), _gc, _fireAt, [_agentA]);

        Assert.Equal(first.ClientMsgId, retry.ClientMsgId);
    }

    [Fact]
    public void A_later_fire_gets_a_different_client_message_id()
    {
        var first = RyuseiPingLine.Build(Trigger(RyuseiPingMode.Attach, _gc), _gc, _fireAt, [_agentA]);
        var later = RyuseiPingLine.Build(Trigger(RyuseiPingMode.Attach, _gc), _gc, _fireAt.AddMinutes(1), [_agentA]);

        Assert.NotEqual(first.ClientMsgId, later.ClientMsgId);
        Assert.True(first.ClientMsgId.Length <= AppendRequest.MaxClientMsgIdLength);
    }

    [Fact]
    public void New_gc_line_goes_to_the_gc_it_is_given()
    {
        var newGc = Guid.Parse("44444444-4444-7444-8444-444444444444");
        var request = RyuseiPingLine.Build(Trigger(RyuseiPingMode.NewGc, null), newGc, _fireAt, [_agentA]);

        Assert.Equal(newGc, request.GcId);
        Assert.Equal(AuthorKind.System, request.AuthorKind);
    }

    [Fact]
    public void Ping_mode_none_posts_no_line()
    {
        Assert.Throws<ArgumentException>(() => RyuseiPingLine.Build(Trigger(RyuseiPingMode.None, null), _gc, _fireAt, [_agentA]));
    }

    [Fact]
    public void Ping_inbox_kind_ranks_with_the_scheduled_wakes()
    {
        Assert.Contains(InboxKind.RyuseiPing, InboxKind.All);
        Assert.Equal(InboxPriority.Rank(InboxKind.RyuseiTimer), InboxPriority.Rank(InboxKind.RyuseiPing));
    }
}
