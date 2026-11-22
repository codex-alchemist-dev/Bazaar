using Bazaar.Approvals;
using Bazaar.Contracts.Soul;
using Bazaar.Souls.Abstractions;

namespace Bazaar.Server.TalentPool.Component.Tests;

public class InstantiationTests
{
    private static TalentTemplate Template(string[]? tools = null) => new(
        "code-reviewer", 1,
        new SoulManifest("Code Reviewer", "Review kindly.", tools ?? ["shell"], ["anthropic:claude-haiku-5-5"], ["soul.md"]),
        new string('a', 64), "agent-a", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Hire_creates_agent_soul_binding_and_exact_model_policy_in_order()
    {
        var log = new List<string>();
        var agentId = Guid.NewGuid();
        var souls = new FakeSouls(log);
        var bindings = new FakeBindings(log);
        var policy = new FakePolicy(log);
        var sut = new SoulTalentInstantiator(new FakeAgents(log, agentId), souls, bindings, policy);

        var id = await sut.CreateAgentAsync(Template(), "owner", TestContext.Current.CancellationToken);

        Assert.Equal(agentId.ToString(), id);
        Assert.Equal(["agent", "soul", "bind", "policy"], log);
        var append = Assert.Single(souls.Appends);
        Assert.Equal(0, append.ExpectedVersion);
        Assert.Equal("code-reviewer@1", append.DecisionRef);
        Assert.Matches("^[0-9a-f]{64}$", append.CharterSha256);
        Assert.Contains("\"toolAllowlist\":[\"shell\"]", append.PreferencesJson);
        Assert.Equal((agentId, append.SoulId), Assert.Single(bindings.Bound));
        Assert.Equal(["anthropic:claude-haiku-5-5"], policy.Saved.Single().Models);
    }

    [Fact]
    public async Task A_failed_soul_append_leaves_no_binding_or_policy()
    {
        var log = new List<string>();
        var souls = new FakeSouls(log) { Fail = true };
        var bindings = new FakeBindings(log);
        var policy = new FakePolicy(log);
        var sut = new SoulTalentInstantiator(new FakeAgents(log, Guid.NewGuid()), souls, bindings, policy);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.CreateAgentAsync(Template(), "owner", TestContext.Current.CancellationToken));

        Assert.Empty(bindings.Bound);
        Assert.Empty(policy.Saved);
    }

    private sealed class FakeAgents(List<string> log, Guid id) : IAgentCreator
    {
        public Task<Guid> CreateAsync(string name, CancellationToken ct) { log.Add("agent"); return Task.FromResult(id); }
    }

    private sealed class FakeSouls(List<string> log) : ISoulVersionAppender
    {
        public bool Fail { get; init; }
        public List<SoulVersionAppend> Appends { get; } = [];
        public Task AppendAsync(SoulVersionAppend append, CancellationToken ct)
        {
            if (Fail) { throw new InvalidOperationException("boom"); }
            log.Add("soul"); Appends.Add(append); return Task.CompletedTask;
        }
    }

    private sealed class FakeBindings(List<string> log) : ISoulBindingAdmin
    {
        public List<(Guid Agent, Guid Soul)> Bound { get; } = [];
        public Task BindAsync(Guid agentId, Guid soulId, string reason, CancellationToken cancellationToken) { log.Add("bind"); Bound.Add((agentId, soulId)); return Task.CompletedTask; }
        public Task RetireAsync(Guid agentId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakePolicy(List<string> log) : IAgentModelPolicyWriter
    {
        public List<(Guid Agent, IReadOnlyList<string> Models)> Saved { get; } = [];
        public Task SaveAsync(Guid agentId, IReadOnlyList<string> models, string changedBy, CancellationToken ct) { log.Add("policy"); Saved.Add((agentId, models)); return Task.CompletedTask; }
    }
}

public class ToolGrantApproverTests
{
    private static readonly Guid _owner = Guid.Parse("0f0f0f0f-0000-4000-8000-000000000001");
    private readonly InMemoryTalentTemplateStore _store = new();
    private readonly Recorder _recorder = new();

    private TalentToolGrantApprover Approver(OwnerSignatureVerdict verdict) => new(_store, new Verifier(verdict), _recorder, TimeProvider.System);

    private async Task<TalentTemplate> Seed(string[] tools)
    {
        var t = new TalentTemplate("code-reviewer", 1, new SoulManifest("Code Reviewer", "p", tools, ["anthropic:claude-haiku-5-5"], []), new string('a', 64), "a", DateTimeOffset.UnixEpoch);
        await _store.AddAsync(t, TestContext.Current.CancellationToken);
        return t;
    }

    private static OwnerPayload Payload(byte[] hash, string action = TalentToolGrantApprover.Action) =>
        new(action, Guid.NewGuid(), hash, null, _owner, "device-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), new byte[OwnerSignedPayload.NonceLength]);

    [Fact]
    public async Task A_valid_owner_signature_over_the_exact_tool_list_records_the_grant()
    {
        var t = await Seed(["shell", "browser"]);
        var result = await Approver(OwnerSignatureVerdict.Valid).ApproveAsync("code-reviewer", 1, Payload(TalentToolGrantApprover.ArgsHashFor(t.HireKey, ["browser", "shell"])), [1], TestContext.Current.CancellationToken);

        Assert.Equal(ToolGrantApprovalResult.Approved, result);
        var (key, tools, by) = Assert.Single(_recorder.Recorded);
        Assert.Equal("code-reviewer@1", key);
        Assert.Equal(["shell", "browser"], tools);
        Assert.Equal(_owner.ToString("D"), by);
    }

    [Theory]
    [InlineData(OwnerSignatureVerdict.BadSignature)]
    [InlineData(OwnerSignatureVerdict.NotTheOwner)]
    [InlineData(OwnerSignatureVerdict.NonceReplayed)]
    public async Task A_refused_signature_records_nothing(OwnerSignatureVerdict verdict)
    {
        var t = await Seed(["shell"]);
        var result = await Approver(verdict).ApproveAsync("code-reviewer", 1, Payload(TalentToolGrantApprover.ArgsHashFor(t.HireKey, ["shell"])), [1], TestContext.Current.CancellationToken);

        Assert.Equal(ToolGrantApprovalResult.SignatureRefused, result);
        Assert.Empty(_recorder.Recorded);
    }

    [Fact]
    public async Task A_signature_over_a_different_tool_list_or_action_is_a_mismatch()
    {
        var t = await Seed(["shell"]);
        var sut = Approver(OwnerSignatureVerdict.Valid);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(ToolGrantApprovalResult.PayloadMismatch, await sut.ApproveAsync("code-reviewer", 1, Payload(TalentToolGrantApprover.ArgsHashFor(t.HireKey, ["shell", "root"])), [1], ct));
        Assert.Equal(ToolGrantApprovalResult.PayloadMismatch, await sut.ApproveAsync("code-reviewer", 1, Payload(TalentToolGrantApprover.ArgsHashFor(t.HireKey, ["shell"]), "pr.merge"), [1], ct));
        Assert.Empty(_recorder.Recorded);
    }

    [Fact]
    public async Task Unknown_template_or_no_tools_has_nothing_to_approve()
    {
        await Seed([]);
        var sut = Approver(OwnerSignatureVerdict.Valid);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(ToolGrantApprovalResult.NothingToApprove, await sut.ApproveAsync("code-reviewer", 1, Payload([1]), [1], ct));
        Assert.Equal(ToolGrantApprovalResult.NothingToApprove, await sut.ApproveAsync("nope", 1, Payload([1]), [1], ct));
    }

    private sealed class Verifier(OwnerSignatureVerdict verdict) : IOwnerSignatureVerifier
    {
        public Task<OwnerSignatureVerdict> VerifyAsync(OwnerPayload payload, byte[] signature, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(verdict);
    }

    private sealed class Recorder : IToolGrantRecorder
    {
        public List<(string Key, IReadOnlyList<string> Tools, string By)> Recorded { get; } = [];
        public Task RecordAsync(string hireKey, IReadOnlyList<string> tools, string approvedBy, CancellationToken ct) { Recorded.Add((hireKey, tools, approvedBy)); return Task.CompletedTask; }
    }
}
