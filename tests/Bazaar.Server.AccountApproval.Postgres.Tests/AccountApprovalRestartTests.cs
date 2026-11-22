using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.AccountApproval.Tests;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.AccountApproval.Postgres.Tests;

/// <summary>
/// The service over a durable store: a new service instance over the same state (a server restart) sees the earlier request and
/// votes. Run over the in-memory store (one shared instance) always, and over Postgres when a test database is available.
/// </summary>
public abstract class AccountApprovalRestartTests
{
    protected abstract Task<StoreHandle> CreateAsync(CancellationToken ct);

    [Fact]
    public async Task A_pending_request_and_its_votes_survive_a_restart_and_the_quorum_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await CreateAsync(ct);
        await using var _ = handle.Cleanup;
        var before = new Harness(store: handle.Open());
        await before.Service.SubmitAsync(Fixtures.Request(), ct);
        await before.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), ct);

        var after = new Harness(store: handle.Open());
        var seen = await after.Service.GetAsync("req-1", ct);
        var second = await after.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), ct);

        var pending = Assert.IsType<Pending>(seen);
        Assert.Equal(1, pending.Remaining);
        Assert.Equal(VoteDisposition.Counted, second.Disposition);
        var approved = Assert.IsType<ApprovedByPeers>(second.Decision);
        Assert.Equal(["agent-a", "agent-b"], approved.Approvals.Select(v => v.ApproverAgentId));
    }

    [Fact]
    public async Task A_vote_cast_before_a_restart_is_not_counted_again_after_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await CreateAsync(ct);
        await using var _ = handle.Cleanup;
        var before = new Harness(store: handle.Open());
        await before.Service.SubmitAsync(Fixtures.Request(), ct);
        await before.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), ct);

        var after = new Harness(store: handle.Open());
        var repeat = await after.Service.RecordVoteAsync(Fixtures.Vote("agent-a"), ct);

        Assert.Equal(VoteDisposition.Duplicate, repeat.Disposition);
        Assert.IsType<Pending>(repeat.Decision);
        Assert.Empty(after.Audit.OfKind("account_approval.peer_vote"));
    }

    [Fact]
    public async Task A_decision_survives_a_restart_and_is_not_audited_or_handed_off_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await CreateAsync(ct);
        await using var _ = handle.Cleanup;
        var before = new Harness(store: handle.Open());
        await before.Service.SubmitAsync(Fixtures.Request(), ct);
        await before.Service.RecordVoteAsync(Fixtures.Vote("agent-a", PeerVoteKind.Refuse), ct);
        Assert.Single(before.HandOff.Requests);

        var after = new Harness(store: handle.Open());
        var resubmitted = await after.Service.SubmitAsync(Fixtures.Request(), ct);
        var late = await after.Service.RecordVoteAsync(Fixtures.Vote("agent-b"), ct);
        var reevaluated = await after.Service.ReevaluateAsync("req-1", ct);

        var owner = Assert.IsType<NeedsOwner>(resubmitted);
        Assert.Equal(DecisionRule.PeerRefused, owner.Rule);
        Assert.Equal("agent-a", Assert.Single(owner.Evidence.PeerVotes).ApproverAgentId);
        Assert.Equal(VoteDisposition.AlreadyDecided, late.Disposition);
        Assert.IsType<NeedsOwner>(reevaluated);
        Assert.Empty(after.HandOff.Requests);
        Assert.Empty(after.Audit.OfKind("account_approval.decision"));
    }

    [Fact]
    public async Task An_expired_pending_request_is_decided_once_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await CreateAsync(ct);
        await using var _ = handle.Cleanup;
        var before = new Harness(store: handle.Open());
        await before.Service.SubmitAsync(Fixtures.Request(), ct);

        var after = new Harness(store: handle.Open());
        after.Time.Current = Fixtures.Now.AddHours(after.Options.ApprovalExpiryHours + 1);
        var first = await after.Service.ReevaluateAsync("req-1", ct);
        var second = await after.Service.ReevaluateAsync("req-1", ct);

        Assert.Equal(DecisionRule.ApprovalExpired, Assert.IsType<NeedsOwner>(first).Rule);
        Assert.IsType<NeedsOwner>(second);
        Assert.Single(after.HandOff.Requests);
        Assert.Single(after.Audit.OfKind("account_approval.decision"));
    }

    [Fact]
    public async Task A_request_id_reused_for_another_service_is_refused_after_a_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var handle = await CreateAsync(ct);
        await using var _ = handle.Cleanup;
        await new Harness(store: handle.Open()).Service.SubmitAsync(Fixtures.Request(), ct);

        var after = new Harness(store: handle.Open());

        await Assert.ThrowsAsync<InvalidOperationException>(() => after.Service.SubmitAsync(Fixtures.Request(service: "other.test"), ct));
    }
}

public sealed class InMemoryAccountApprovalRestartTests : AccountApprovalRestartTests
{
    protected override Task<StoreHandle> CreateAsync(CancellationToken ct)
    {
        var store = new InMemoryAccountApprovalStore();
        return Task.FromResult(new StoreHandle(() => store, NoCleanup.Instance));
    }
}

public sealed class PostgresAccountApprovalRestartTests : AccountApprovalRestartTests
{
    protected override async Task<StoreHandle> CreateAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await ApprovalDatabase.CreateAsync(ct);
        return new StoreHandle(() => new PostgresAccountApprovalStore(db.DataSource), db);
    }
}
