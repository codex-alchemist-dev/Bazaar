using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.ApprovalQueue.Tests;

public sealed class ApprovalQueueServiceTests
{
    private static readonly Guid _requester = Guid.NewGuid();
    private static readonly Guid _humanAdmin = Guid.NewGuid();
    private static readonly Guid _agent = Guid.NewGuid();
    private static readonly Guid _humanWithoutGrant = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryApprovalStore _store = new();
    private readonly FakeRoles _roles = new();
    private readonly RecordingAudit _audit = new();
    private readonly ApprovalQueueService _queue;

    public ApprovalQueueServiceTests()
    {
        _roles.Assign(_humanAdmin, PrincipalKind.Human, grant: true);
        _roles.Assign(_humanWithoutGrant, PrincipalKind.Human, grant: false);
        _roles.Assign(_agent, PrincipalKind.Agent, grant: true);
        _queue = new ApprovalQueueService(_store, _roles, _roles, _audit, _time);
    }

    private Task<Guid> RequestAsync(int tier = 1, TimeSpan? ttl = null, string kind = "mail.send") =>
        _queue.RequestAsync(new NewApproval(kind, tier, "Send quarterly note", _requester, "mail:draft-1", ttl ?? TimeSpan.FromHours(1)));

    [Fact]
    public async Task Request_files_a_pending_item_and_audits_it_without_contents()
    {
        var id = await RequestAsync();

        var item = await _queue.GetAsync(id);
        Assert.Equal(ApprovalStatus.Pending, item!.Status);
        Assert.Equal(_time.GetUtcNow().AddHours(1), item.ExpiresAt);
        var entry = Assert.Single(_audit.Entries);
        Assert.Equal("approval.requested", entry.Kind);
        Assert.DoesNotContain("quarterly", entry.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("draft-1", entry.Payload, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(entry.Payload);
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Request_refuses_a_tier_other_than_1_or_2(int tier)
    {
        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => RequestAsync(tier));
        Assert.Equal(ApprovalRefusal.InvalidRequest, ex.Refusal);
    }

    [Fact]
    public async Task Ttl_is_clamped_to_one_minute_and_thirty_days()
    {
        var shortId = await RequestAsync(ttl: TimeSpan.FromSeconds(1));
        var longId = await RequestAsync(ttl: TimeSpan.FromDays(400));

        Assert.Equal(_time.GetUtcNow().AddMinutes(1), (await _queue.GetAsync(shortId))!.ExpiresAt);
        Assert.Equal(_time.GetUtcNow().AddDays(30), (await _queue.GetAsync(longId))!.ExpiresAt);
    }

    [Fact]
    public async Task Approve_then_consume_works_once()
    {
        var id = await RequestAsync();

        var decided = await _queue.DecideAsync(id, _humanAdmin, approve: true, "ok");

        Assert.Equal(ApprovalStatus.Approved, decided.Status);
        Assert.Equal(_humanAdmin, decided.DecidedBy);
        Assert.True(await _queue.TryConsumeAsync(id));
        Assert.False(await _queue.TryConsumeAsync(id));
        Assert.Equal(ApprovalStatus.Consumed, (await _queue.GetAsync(id))!.Status);
        Assert.Equal(["approval.requested", "approval.decided", "approval.consumed"], _audit.Entries.Select(e => e.Kind));
    }

    [Fact]
    public async Task Concurrent_consumes_succeed_exactly_once()
    {
        var id = await RequestAsync();
        await _queue.DecideAsync(id, _humanAdmin, true, null);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => _queue.TryConsumeAsync(id))));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task Pending_and_denied_items_cannot_be_consumed()
    {
        var pending = await RequestAsync();
        var denied = await RequestAsync();
        await _queue.DecideAsync(denied, _humanAdmin, approve: false, "no");

        Assert.False(await _queue.TryConsumeAsync(pending));
        Assert.False(await _queue.TryConsumeAsync(denied));
        Assert.False(await _queue.TryConsumeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task A_pending_item_reads_expired_after_its_ttl_and_cannot_be_decided()
    {
        var id = await RequestAsync(ttl: TimeSpan.FromMinutes(5));
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(ApprovalStatus.Expired, (await _queue.GetAsync(id))!.Status);
        Assert.Empty(await _queue.ListPendingAsync(null));
        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => _queue.DecideAsync(id, _humanAdmin, true, null));
        Assert.Equal(ApprovalRefusal.NotPending, ex.Refusal);
    }

    [Fact]
    public async Task The_requester_may_not_decide_their_own_request()
    {
        _roles.Assign(_requester, PrincipalKind.Human, grant: true);
        var id = await RequestAsync();

        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => _queue.DecideAsync(id, _requester, true, null));

        Assert.Equal(ApprovalRefusal.SelfApproval, ex.Refusal);
        Assert.Equal(ApprovalStatus.Pending, (await _queue.GetAsync(id))!.Status);
    }

    [Fact]
    public async Task An_approver_without_the_capability_is_refused()
    {
        var id = await RequestAsync();

        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => _queue.DecideAsync(id, _humanWithoutGrant, true, null));
        var unknown = await Assert.ThrowsAsync<ApprovalRefusedException>(() => _queue.DecideAsync(id, Guid.NewGuid(), true, null));

        Assert.Equal(ApprovalRefusal.NotPermitted, ex.Refusal);
        Assert.Equal(ApprovalRefusal.NotPermitted, unknown.Refusal);
    }

    [Fact]
    public async Task Tier_2_needs_a_human_approver()
    {
        // The fake checker is generous to agents on purpose; the queue's own tier 2 rule must still refuse.
        _roles.AllowAgents = true;
        var id = await RequestAsync(tier: 2);

        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => _queue.DecideAsync(id, _agent, true, null));
        var ok = await _queue.DecideAsync(id, _humanAdmin, true, null);

        Assert.Equal(ApprovalRefusal.HumanRequired, ex.Refusal);
        Assert.Equal(ApprovalStatus.Approved, ok.Status);
    }

    [Fact]
    public async Task A_decided_item_cannot_be_decided_again()
    {
        var id = await RequestAsync();
        await _queue.DecideAsync(id, _humanAdmin, false, "no");

        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => _queue.DecideAsync(id, _humanAdmin, true, null));

        Assert.Equal(ApprovalRefusal.NotPending, ex.Refusal);
    }

    [Fact]
    public async Task Deciding_an_unknown_item_is_not_found()
    {
        var ex = await Assert.ThrowsAsync<ApprovalRefusedException>(() => _queue.DecideAsync(Guid.NewGuid(), _humanAdmin, true, null));
        Assert.Equal(ApprovalRefusal.NotFound, ex.Refusal);
    }

    [Fact]
    public async Task List_pending_filters_by_kind_and_hides_decided_items()
    {
        var a = await RequestAsync(kind: "mail.send");
        await RequestAsync(kind: "git.merge");
        var c = await RequestAsync(kind: "mail.send");
        await _queue.DecideAsync(c, _humanAdmin, true, null);

        var mail = await _queue.ListPendingAsync("mail.send");

        Assert.Equal([a], mail.Select(i => i.Id));
        Assert.Equal(2, (await _queue.ListPendingAsync(null)).Count);
    }

    [Fact]
    public async Task The_admin_source_lists_pending_items()
    {
        var id = await RequestAsync();

        var rows = await new AdminApprovalSource(_queue).ListPendingAsync();

        var row = Assert.Single(rows);
        Assert.Equal(id.ToString("D"), row.Id);
        Assert.Equal(_requester.ToString("D"), row.Requester);
        Assert.StartsWith("mail.send", row.Action, StringComparison.Ordinal);
        Assert.DoesNotContain("draft-1", row.Action, StringComparison.Ordinal);
    }
}
