using Bazaar.Server.AccountApproval.Abstractions;
using Bazaar.Server.AccountApproval.Tests;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.AccountApproval.Postgres.Tests;

/// <summary>A store under test: <see cref="Open"/> returns a new store instance over the same state, as after a restart.</summary>
public sealed record StoreHandle(Func<IAccountApprovalStore> Open, IAsyncDisposable Cleanup);

/// <summary>
/// What every <see cref="IAccountApprovalStore"/> must do. Run against the in-memory store always and against Postgres when a test
/// database is available.
/// </summary>
public abstract class AccountApprovalStoreContractTests
{
    protected abstract Task<StoreHandle> CreateAsync(CancellationToken ct);

    private static readonly AccountApprovalRequest _request = Fixtures.Request();

    private static Pending Waiting(int remaining = 2) => new("req-1", remaining, 2);

    [Fact]
    public async Task An_unknown_request_reads_as_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);

        Assert.Null(await h.Open().GetAsync("nope", ct));
    }

    [Fact]
    public async Task A_created_request_reads_back_with_every_field()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var site = new SiteVoteResult(SiteVoteResult.SiteAllowlistMotion, "example.test", "vote-9", true, 4, 4, 0, 0);
        var request = _request with { SiteVote = site, PersonSteps = PersonStep.Captcha | PersonStep.Phone };

        Assert.True(await h.Open().TryCreateAsync(request, Waiting(), ct));

        var read = await h.Open().GetAsync("req-1", ct);
        Assert.NotNull(read);
        Assert.Equal(request, read.Request);
        Assert.Empty(read.Votes);
        Assert.Equal(Waiting(), read.Decision);
    }

    [Fact]
    public async Task Creating_the_same_id_twice_keeps_the_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();
        Assert.True(await store.TryCreateAsync(_request, Waiting(), ct));

        Assert.False(await store.TryCreateAsync(_request with { Service = "other.test" }, new Denied("req-1", DecisionRule.UnverifiedTerms), ct));

        var read = await store.GetAsync("req-1", ct);
        Assert.Equal("example.test", read!.Request.Service);
        Assert.IsType<Pending>(read.Decision);
    }

    [Fact]
    public async Task A_vote_and_its_decision_are_stored_together_and_listed_in_order()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();
        await store.TryCreateAsync(_request, Waiting(), ct);

        Assert.Equal(StoredVoteOutcome.Stored, await store.TryAppendVoteAsync(Fixtures.Vote("agent-a"), Waiting(1), ct));
        Assert.Equal(StoredVoteOutcome.Stored, await store.TryAppendVoteAsync(Fixtures.Vote("agent-b"), Waiting(0), ct));

        var read = await h.Open().GetAsync("req-1", ct);
        Assert.Equal(["agent-a", "agent-b"], read!.Votes.Select(v => v.ApproverAgentId));
        Assert.Equal(Waiting(0), read.Decision);
        Assert.All(read.Votes, v => Assert.Equal("example.test", v.Service));
        Assert.Equal(Fixtures.Vote("agent-a"), read.Votes[0]);
    }

    [Fact]
    public async Task The_same_approver_and_kind_twice_is_refused_and_counts_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();
        await store.TryCreateAsync(_request, Waiting(), ct);
        await store.TryAppendVoteAsync(Fixtures.Vote("agent-a"), Waiting(1), ct);

        var again = await store.TryAppendVoteAsync(Fixtures.Vote("agent-a"), Waiting(0), ct);

        Assert.Equal(StoredVoteOutcome.DuplicateVote, again);
        var read = await store.GetAsync("req-1", ct);
        Assert.Single(read!.Votes);
        Assert.Equal(Waiting(1), read.Decision);
    }

    [Fact]
    public async Task Concurrent_identical_votes_are_counted_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();
        await store.TryCreateAsync(_request, Waiting(), ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.TryAppendVoteAsync(Fixtures.Vote("agent-a"), Waiting(1), ct)));

        Assert.Equal(1, results.Count(r => r == StoredVoteOutcome.Stored));
        Assert.Equal(7, results.Count(r => r == StoredVoteOutcome.DuplicateVote));
        Assert.Single((await store.GetAsync("req-1", ct))!.Votes);
    }

    [Fact]
    public async Task A_vote_on_an_unknown_request_stores_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);

        Assert.Equal(StoredVoteOutcome.UnknownRequest, await h.Open().TryAppendVoteAsync(Fixtures.Vote("agent-a", id: "ghost"), Waiting(), ct));
        Assert.Null(await h.Open().GetAsync("ghost", ct));
    }

    [Fact]
    public async Task A_decided_request_takes_no_more_votes_and_no_second_decision()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();
        await store.TryCreateAsync(_request, Waiting(), ct);
        var refuse = Fixtures.Vote("agent-a", PeerVoteKind.Refuse);
        var owner = new NeedsOwner("req-1", DecisionRule.PeerRefused, new ApprovalEvidence("example.test", "agent-r", _request.TermsUrl, null, [refuse]));
        Assert.Equal(StoredVoteOutcome.Stored, await store.TryAppendVoteAsync(refuse, owner, ct));

        Assert.Equal(StoredVoteOutcome.AlreadyDecided, await store.TryAppendVoteAsync(Fixtures.Vote("agent-b"), Waiting(0), ct));
        Assert.False(await store.TryDecideAsync("req-1", new Denied("req-1", DecisionRule.UnverifiedTerms), ct));

        var read = await store.GetAsync("req-1", ct);
        Assert.Single(read!.Votes);
        var kept = Assert.IsType<NeedsOwner>(read.Decision);
        Assert.Equal(DecisionRule.PeerRefused, kept.Rule);
        Assert.Equal(refuse, Assert.Single(kept.Evidence.PeerVotes));
    }

    [Fact]
    public async Task A_pending_request_can_be_decided_once_and_the_decision_reads_back()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();
        await store.TryCreateAsync(_request, Waiting(), ct);
        await store.TryAppendVoteAsync(Fixtures.Vote("agent-a"), Waiting(1), ct);
        await store.TryAppendVoteAsync(Fixtures.Vote("agent-b"), Waiting(0), ct);
        var votes = (await store.GetAsync("req-1", ct))!.Votes;
        var approved = new ApprovedByPeers(
            "req-1", DecisionRule.PeerQuorumReached, votes, PersonStep.Captcha | PersonStep.LegalTerms, false, null, null, Fixtures.Now.AddHours(24));

        Assert.True(await store.TryDecideAsync("req-1", approved, ct));
        Assert.False(await store.TryDecideAsync("req-1", approved, ct));

        var read = Assert.IsType<ApprovedByPeers>((await h.Open().GetAsync("req-1", ct))!.Decision);
        Assert.Equal(votes, read.Approvals);
        Assert.Equal(approved.PersonSteps, read.PersonSteps);
        Assert.Equal(approved.ExpiresAt, read.ExpiresAt);
        Assert.False(read.AgentMayAcceptTerms);
        Assert.Null(read.TermsToAccept);
    }

    [Fact]
    public async Task Terms_the_agent_may_accept_survive_in_an_approval()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();
        await store.TryCreateAsync(_request, Waiting(), ct);
        await store.TryAppendVoteAsync(Fixtures.Vote("agent-a"), Waiting(0), ct);
        var votes = (await store.GetAsync("req-1", ct))!.Votes;

        await store.TryDecideAsync(
            "req-1",
            new ApprovedByPeers("req-1", DecisionRule.PeerQuorumReached, votes, PersonStep.None, true, "https://example.test/terms", "v1", Fixtures.Now.AddHours(1)),
            ct);

        var read = Assert.IsType<ApprovedByPeers>((await store.GetAsync("req-1", ct))!.Decision);
        Assert.True(read.AgentMayAcceptTerms);
        Assert.Equal("https://example.test/terms", read.TermsToAccept);
        Assert.Equal("v1", read.TermsVersionOrHash);
    }

    [Fact]
    public async Task A_denied_request_reads_back_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Own(ct);
        var store = h.Open();

        Assert.True(await store.TryCreateAsync(_request, new Denied("req-1", DecisionRule.TermsForbidAutomation), ct));

        var read = Assert.IsType<Denied>((await h.Open().GetAsync("req-1", ct))!.Decision);
        Assert.Equal(DecisionRule.TermsForbidAutomation, read.Rule);
    }

    private async Task<OwnedHandle> Own(CancellationToken ct) => new(await CreateAsync(ct));

    /// <summary>Disposes the store's cleanup at the end of a test.</summary>
    private sealed class OwnedHandle(StoreHandle handle) : IAsyncDisposable
    {
        public Func<IAccountApprovalStore> Open => handle.Open;

        public ValueTask DisposeAsync() => handle.Cleanup.DisposeAsync();
    }
}

public sealed class InMemoryAccountApprovalStoreContractTests : AccountApprovalStoreContractTests
{
    protected override Task<StoreHandle> CreateAsync(CancellationToken ct)
    {
        var store = new InMemoryAccountApprovalStore();
        return Task.FromResult(new StoreHandle(() => store, NoCleanup.Instance));
    }
}

public sealed class PostgresAccountApprovalStoreContractTests : AccountApprovalStoreContractTests
{
    protected override async Task<StoreHandle> CreateAsync(CancellationToken ct)
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var db = await ApprovalDatabase.CreateAsync(ct);
        return new StoreHandle(() => new PostgresAccountApprovalStore(db.DataSource), db);
    }
}

internal sealed class NoCleanup : IAsyncDisposable
{
    public static NoCleanup Instance { get; } = new();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
