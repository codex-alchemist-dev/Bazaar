using Bazaar.Server.Council;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.Council.Membership;
using Bazaar.Server.Council.Service;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.TalentPool.Tests;

public sealed class TalentPoolTests : IDisposable
{
    private static readonly Guid _gc = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000002");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 22, 18, 0, 0, TimeSpan.Zero));
    private readonly FakeCouncil _council = new();
    private readonly MemStore _decisions = new();
    private readonly Whitelist _whitelist = new("claude-sonnet-5-5", "claude-haiku-5-5");
    private readonly Approval _toolApproval = new();
    private readonly Instantiator _instantiator = new();
    private readonly TalentPoolService _pool;
    private readonly HireOutcomeSink _sink;

    public void Dispose() => _pool.Dispose();

    public TalentPoolTests()
    {
        var gate = new CouncilTalentHireGate(_council, _decisions, new HireMotionDraftBuilder(new HireMotionClassifier(null)), _time);
        _pool = new TalentPoolService(new InMemoryTalentTemplateStore(), gate, _decisions, _whitelist, _toolApproval, _instantiator, _time);
        _sink = new HireOutcomeSink(_decisions, _time);
    }

    private static SoulManifest Manifest(string name = "Code Reviewer", string[]? tools = null, string[]? files = null, string prompt = "Review code kindly.", string[]? models = null) =>
        new(name, prompt, tools ?? [], models ?? ["claude-haiku-5-5"], files ?? ["soul.md"], "soul-1");

    private async Task Decide(Guid voteId, string key, MembershipDecision decision, string? choice) =>
        await _sink.PublishAsync(new MembershipOutcome(voteId, key, MembershipMotionKind.Hire, decision, choice, "ref", _time.GetUtcNow(), null), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Submit_stores_a_quarantined_version_and_detects_duplicates()
    {
        var first = await _pool.SubmitAsync(Manifest(), "agent-a", TestContext.Current.CancellationToken);
        var again = await _pool.SubmitAsync(Manifest(), "agent-b", TestContext.Current.CancellationToken);
        var changed = await _pool.SubmitAsync(Manifest(prompt: "Review code sternly."), "agent-a", TestContext.Current.CancellationToken);

        Assert.Equal("code-reviewer", first.TemplateId);
        Assert.Equal(1, first.Version);
        Assert.True(again.IsDuplicate);
        Assert.Equal(1, again.Version);
        Assert.Equal(2, changed.Version);
        Assert.Equal(TemplateStanding.Quarantined, (await _pool.GetAsync("code-reviewer", 1, TestContext.Current.CancellationToken))!.Standing);
        Assert.Single(await _pool.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Memory_files_and_off_whitelist_models_are_rejected()
    {
        await Assert.ThrowsAsync<ManifestRejectedException>(() => _pool.SubmitAsync(Manifest(files: ["soul.md", "memory/lessons.md"]), "a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ManifestRejectedException>(() => _pool.SubmitAsync(Manifest(models: ["some-other-model"]), "a", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ManifestRejectedException>(() => _pool.SubmitAsync(Manifest(prompt: " "), "a", TestContext.Current.CancellationToken));
        Assert.Empty(await _pool.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hire_flow_runs_through_the_council_and_releases_only_on_approve()
    {
        await _pool.SubmitAsync(Manifest(), "agent-a", TestContext.Current.CancellationToken);

        // Quarantined: instantiation refused before any vote.
        await Assert.ThrowsAsync<InstantiationRefusedException>(() => _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken));

        var standing = await _pool.ProposeHireAsync("code-reviewer", 1, _gc, "agent-a", _time.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken);
        Assert.Equal(TemplateStanding.VotePending, standing);
        var (_, draft, voteId) = Assert.Single(_council.Opened);
        Assert.Equal("code-reviewer@1", draft.SubjectRef);

        // Re-proposing while open does not open a second vote.
        await _pool.ProposeHireAsync("code-reviewer", 1, _gc, "agent-a", _time.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken);
        Assert.Single(_council.Opened);
        await Assert.ThrowsAsync<InstantiationRefusedException>(() => _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken));

        await Decide(voteId, "code-reviewer@1", MembershipDecision.Approved, ApprovalChoices.Yes);

        Assert.Equal(TemplateStanding.Released, (await _pool.GetAsync("code-reviewer", 1, TestContext.Current.CancellationToken))!.Standing);
        var agentId = await _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken);
        Assert.Equal("agent-of-code-reviewer@1", agentId);
    }

    [Fact]
    public async Task Rejected_vote_blocks_and_can_be_proposed_again()
    {
        await _pool.SubmitAsync(Manifest(), "agent-a", TestContext.Current.CancellationToken);
        await _pool.ProposeHireAsync("code-reviewer", 1, _gc, "agent-a", _time.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken);
        await Decide(_council.Opened[0].VoteId, "code-reviewer@1", MembershipDecision.Rejected, ApprovalChoices.No);

        Assert.Equal(TemplateStanding.Blocked, (await _pool.GetAsync("code-reviewer", 1, TestContext.Current.CancellationToken))!.Standing);
        await Assert.ThrowsAsync<InstantiationRefusedException>(() => _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken));

        Assert.Equal(TemplateStanding.VotePending, await _pool.ProposeHireAsync("code-reviewer", 1, _gc, "agent-a", _time.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken));
        Assert.Equal(2, _council.Opened.Count);
    }

    [Fact]
    public async Task A_new_version_needs_its_own_vote()
    {
        await _pool.SubmitAsync(Manifest(), "agent-a", TestContext.Current.CancellationToken);
        await _pool.ProposeHireAsync("code-reviewer", 1, _gc, "agent-a", _time.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken);
        await Decide(_council.Opened[0].VoteId, "code-reviewer@1", MembershipDecision.Approved, ApprovalChoices.Yes);

        await _pool.SubmitAsync(Manifest(prompt: "Changed prompt."), "agent-a", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InstantiationRefusedException>(() => _pool.InstantiateAsync("code-reviewer", 2, "owner", TestContext.Current.CancellationToken));
        Assert.NotNull(await _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tool_grants_need_tier_two_approval_beyond_the_vote()
    {
        await _pool.SubmitAsync(Manifest(tools: ["shell"]), "agent-a", TestContext.Current.CancellationToken);
        await _pool.ProposeHireAsync("code-reviewer", 1, _gc, "agent-a", _time.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken);
        await Decide(_council.Opened[0].VoteId, "code-reviewer@1", MembershipDecision.Approved, ApprovalChoices.Yes);

        await Assert.ThrowsAsync<InstantiationRefusedException>(() => _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken));

        _toolApproval.Approved = true;
        Assert.NotNull(await _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Model_dropped_from_whitelist_is_refused_at_instantiation()
    {
        await _pool.SubmitAsync(Manifest(), "agent-a", TestContext.Current.CancellationToken);
        await _pool.ProposeHireAsync("code-reviewer", 1, _gc, "agent-a", _time.GetUtcNow().AddDays(1), TestContext.Current.CancellationToken);
        await Decide(_council.Opened[0].VoteId, "code-reviewer@1", MembershipDecision.Approved, ApprovalChoices.Yes);

        _whitelist.Allowed.Remove("claude-haiku-5-5");

        await Assert.ThrowsAsync<InstantiationRefusedException>(() => _pool.InstantiateAsync("code-reviewer", 1, "owner", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_template_cannot_be_proposed()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _pool.ProposeHireAsync("nope", 1, _gc, "a", _time.GetUtcNow(), TestContext.Current.CancellationToken));
    }

    private sealed class FakeCouncil : ICouncilService
    {
        public List<(string Caller, MotionDraft Draft, Guid VoteId)> Opened { get; } = [];

        public Task<Guid> OpenAsync(string callerId, MotionDraft motion, CancellationToken ct)
        {
            var id = Guid.NewGuid();
            Opened.Add((callerId, motion, id));
            return Task.FromResult(id);
        }

        public Task SubmitBallotAsync(Guid voteId, string voterId, Ballot ballot, CancellationToken ct) => throw new NotSupportedException();
        public Task ResolveEscalationAsync(Guid voteId, string optionChoice, string? reasoning, CancellationToken ct) => throw new NotSupportedException();
        public Task ProcessDeadlineAsync(Guid voteId, CancellationToken ct) => throw new NotSupportedException();
        public Task WithdrawAsync(Guid voteId, string actorId, CancellationToken ct) => throw new NotSupportedException();
        public Task RedriveDecisionAsync(Guid voteId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class MemStore : IHireDecisionStore
    {
        private readonly Dictionary<string, HireVoteState> _s = new(StringComparer.Ordinal);
        public Task<HireVoteState?> FindAsync(string templateId, CancellationToken ct) => Task.FromResult(_s.GetValueOrDefault(templateId));
        public Task SaveAsync(HireVoteState state, CancellationToken ct) { _s[state.TemplateId] = state; return Task.CompletedTask; }
    }

    private sealed class Whitelist(params string[] models) : ITalentModelWhitelist
    {
        public HashSet<string> Allowed { get; } = [.. models];
        public bool IsAllowed(string model) => Allowed.Contains(model);
    }

    private sealed class Approval : ITalentToolGrantApproval
    {
        public bool Approved { get; set; }
        public Task<bool> IsApprovedAsync(string hireKey, IReadOnlyList<string> tools, CancellationToken ct) => Task.FromResult(Approved);
    }

    private sealed class Instantiator : ITalentInstantiator
    {
        public Task<string> CreateAgentAsync(TalentTemplate template, string actorId, CancellationToken ct) => Task.FromResult($"agent-of-{template.HireKey}");
    }
}
