using System.Collections.Immutable;
using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Taint;
using SessionTaint = Bazaar.Server.CodingPermissions.SessionTaint;

namespace Bazaar.Server.Host.Tests.Coding;

/// <summary>Approval grant lookup and single-use consumption for a push (doc 17, "Approval grant lookup").</summary>
public sealed class PushApprovalGrantsTests : IDisposable
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private const string _agent = "agent-1";
    private const string _device = "device-1";
    private const string _owner = "owner";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _session = Guid.NewGuid();
    private readonly InMemoryApprovalStore _store = new();
    private readonly InMemoryOwnerKeyRegistry _keys = new();
    private readonly PermissiveCandidates _candidates = new();
    private readonly FixedTime _time = new(_now);

    public PushApprovalGrantsTests() =>
        _keys.Register(new OwnerDeviceKey(_owner, _device, _key.ExportSubjectPublicKeyInfo()));

    public void Dispose() => _key.Dispose();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Returns every id it was told about, so the grant code's own checks are what the tests exercise.</summary>
    private sealed class PermissiveCandidates : IApprovedPushApprovalIds
    {
        public List<Guid> Ids { get; } = [];

        public bool Throws { get; set; }

        public ValueTask<IReadOnlyList<Guid>> FindIdsAsync(string agentId, Guid sessionId, string tool, byte[] argsHash, DateTimeOffset now, CancellationToken cancellationToken) =>
            Throws ? throw new InvalidOperationException("down") : ValueTask.FromResult<IReadOnlyList<Guid>>([.. Ids]);
    }

    private sealed class ThrowingStore(IApprovalStore inner, bool getThrows = false, bool replaceThrows = false) : IApprovalStore
    {
        public Task InsertAsync(ApprovalRequest request, CancellationToken ct = default) => inner.InsertAsync(request, ct);

        public Task<ApprovalRequest?> GetAsync(Guid id, CancellationToken ct = default) =>
            getThrows ? throw new InvalidOperationException("down") : inner.GetAsync(id, ct);

        public Task<bool> TryReplaceAsync(Guid id, ApprovalStatus expected, ApprovalRequest updated, CancellationToken ct = default) =>
            replaceThrows ? throw new InvalidOperationException("down") : inner.TryReplaceAsync(id, expected, updated, ct);
    }

    private static PushGateRequest Push(string agent = _agent, string repo = "repo1", string branch = "agent/fix-x", string gc = "gc-1", string task = "fix-x") =>
        new(agent, new RepoRef(repo, "main"), BranchName.Parse(branch), gc, task);

    private ApprovalStorePushGrants Grants(IApprovalStore? store = null) =>
        new(_candidates, store ?? _store, _keys, _time, TimeSpan.FromSeconds(2));

    /// <summary>Stores an approval for the default push, signed by the owner device unless told otherwise.</summary>
    private async Task<ApprovalRequest> ApprovedAsync(
        Guid? session = null,
        ApprovalStatus status = ApprovalStatus.Approved,
        TimeSpan? expiresIn = null,
        bool signed = true,
        string? agentOverride = null,
        ECDsa? signer = null,
        bool consumed = false)
    {
        var push = Push();
        var hash = PermissionGatePushGate.DispatchHashOf(push);
        var id = Guid.CreateVersion7();
        var expires = _now + (expiresIn ?? TimeSpan.FromHours(1));
        byte[]? signature = null;
        if (signed)
        {
            signature = (signer ?? _key).SignData(
                ApprovalSignature.Message(id, ApprovalDecision.Approve, hash, expires, _device),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        var row = new ApprovalRequest(
            id,
            agentOverride ?? push.AgentId,
            session ?? _session,
            PermissionGatePushGate.ToolName,
            push.Branch.Value,
            hash,
            "{}",
            ImmutableArray<TaintSource>.Empty,
            status,
            _now - TimeSpan.FromMinutes(5),
            expires,
            DecidedBy: signed ? _owner : null,
            DeviceId: signed ? _device : null,
            DecidedAt: signed ? _now - TimeSpan.FromMinutes(1) : null,
            Signature: signature,
            ConsumedAt: consumed ? _now - TimeSpan.FromSeconds(30) : null);
        await _store.InsertAsync(row, _ct);
        _candidates.Ids.Add(id);
        return row;
    }

    private ValueTask<ToolApprovalGrant?> FindAsync(PushGateRequest? push = null, IApprovalStore? store = null) =>
        Grants(store).FindAsync(push ?? Push(), _session.ToString("D"), _ct);

    [Fact]
    public async Task An_owner_approval_for_this_exact_push_is_the_grant()
    {
        var row = await ApprovedAsync();

        var grant = await FindAsync();

        Assert.NotNull(grant);
        Assert.Equal(row.Id, grant.ApprovalId);
        Assert.Equal(row.ExpiresAt, grant.ExpiresAt);
        Assert.True(grant.ArgsHash.AsSpan().SequenceEqual(row.ArgsHash));
    }

    [Fact]
    public async Task With_no_approval_there_is_no_grant() => Assert.Null(await FindAsync());

    [Theory]
    [InlineData("agent")]
    [InlineData("repo")]
    [InlineData("branch")]
    [InlineData("gc")]
    [InlineData("task")]
    public async Task An_approval_for_another_agent_repo_branch_gc_or_task_is_not_a_grant(string what)
    {
        // The approval is for the default push; the push being judged differs in one field.
        await ApprovedAsync();
        var other = what switch
        {
            "agent" => Push(agent: "agent-2"),
            "repo" => Push(repo: "repo2"),
            "branch" => Push(branch: "agent/other"),
            "gc" => Push(gc: "gc-2"),
            _ => Push(task: "other"),
        };

        Assert.Null(await FindAsync(other));
    }

    [Fact]
    public async Task An_approval_row_filed_for_another_agent_is_not_a_grant_even_when_the_hash_matches()
    {
        await ApprovedAsync(agentOverride: "agent-2");

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task An_approval_for_another_session_is_not_a_grant()
    {
        await ApprovedAsync(session: Guid.NewGuid());

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task A_session_id_that_is_not_a_guid_has_no_grant()
    {
        await ApprovedAsync();

        Assert.Null(await Grants().FindAsync(Push(), "not-a-guid", _ct));
    }

    [Fact]
    public async Task An_expired_approval_is_not_a_grant()
    {
        await ApprovedAsync(expiresIn: TimeSpan.FromSeconds(10));
        _time.Now = _now + TimeSpan.FromSeconds(10);

        Assert.Null(await FindAsync());
    }

    [Theory]
    [InlineData(ApprovalStatus.Pending)]
    [InlineData(ApprovalStatus.Denied)]
    [InlineData(ApprovalStatus.Expired)]
    [InlineData(ApprovalStatus.Consumed)]
    public async Task Only_an_approved_row_is_a_grant(ApprovalStatus status)
    {
        await ApprovedAsync(status: status);

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task A_row_marked_consumed_while_still_approved_is_not_a_grant()
    {
        await ApprovedAsync(consumed: true);

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task An_approval_that_was_consumed_is_gone()
    {
        await ApprovedAsync();
        var grant = await FindAsync();
        Assert.True(await Grants().ConsumeAsync(grant!, _ct));

        Assert.Null(await FindAsync());
        Assert.False(await Grants().ConsumeAsync(grant!, _ct));
    }

    [Fact]
    public async Task Concurrent_consumption_of_one_grant_succeeds_exactly_once()
    {
        await ApprovedAsync();
        var grant = (await FindAsync())!;

        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await Grants().ConsumeAsync(grant, _ct), _ct)));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task An_approval_created_by_the_agent_without_an_owner_decision_is_not_a_grant()
    {
        await ApprovedAsync(signed: false);

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task An_approval_signed_by_a_key_the_owner_never_registered_is_not_a_grant()
    {
        using var rogue = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await ApprovedAsync(signer: rogue);

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task An_approval_signed_by_a_revoked_owner_device_is_not_a_grant()
    {
        await ApprovedAsync();
        _keys.Register(new OwnerDeviceKey(_owner, _device, _key.ExportSubjectPublicKeyInfo(), Revoked: true));

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task An_approval_whose_decider_is_not_the_keys_owner_is_not_a_grant()
    {
        var row = await ApprovedAsync();
        await _store.TryReplaceAsync(row.Id, ApprovalStatus.Approved, row with { DecidedBy = "someone-else" }, _ct);

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task An_approval_signature_of_the_wrong_length_is_not_a_grant()
    {
        var row = await ApprovedAsync();
        await _store.TryReplaceAsync(row.Id, ApprovalStatus.Approved, row with { Signature = new byte[10] }, _ct);

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task A_store_read_failure_means_no_grant()
    {
        await ApprovedAsync();

        Assert.Null(await FindAsync(store: new ThrowingStore(_store, getThrows: true)));
    }

    [Fact]
    public async Task A_candidate_source_failure_means_no_grant()
    {
        await ApprovedAsync();
        _candidates.Throws = true;

        Assert.Null(await FindAsync());
    }

    [Fact]
    public async Task A_store_failure_while_consuming_means_not_consumed()
    {
        await ApprovedAsync();
        var grant = (await FindAsync())!;

        Assert.False(await Grants(new ThrowingStore(_store, replaceThrows: true)).ConsumeAsync(grant, _ct));
        Assert.NotNull(await FindAsync());
    }

    [Fact]
    public async Task A_grant_that_expired_before_it_was_spent_is_not_consumed()
    {
        await ApprovedAsync(expiresIn: TimeSpan.FromSeconds(10));
        var grant = (await FindAsync())!;
        _time.Now = _now + TimeSpan.FromSeconds(11);

        Assert.False(await Grants().ConsumeAsync(grant, _ct));
    }

    [Fact]
    public async Task A_grant_with_a_changed_hash_is_not_consumed()
    {
        var row = await ApprovedAsync();

        Assert.False(await Grants().ConsumeAsync(new ToolApprovalGrant(row.Id, new byte[32], row.ExpiresAt), _ct));
    }

    [Fact]
    public async Task With_several_matching_owner_approvals_the_earliest_expiring_one_is_chosen()
    {
        await ApprovedAsync(expiresIn: TimeSpan.FromHours(5));
        var soon = await ApprovedAsync(expiresIn: TimeSpan.FromHours(1));

        Assert.Equal(soon.Id, (await FindAsync())!.ApprovalId);
    }

    [Fact]
    public async Task A_bad_row_among_good_ones_is_skipped()
    {
        await ApprovedAsync(signed: false, expiresIn: TimeSpan.FromMinutes(1));
        var good = await ApprovedAsync(expiresIn: TimeSpan.FromHours(1));

        Assert.Equal(good.Id, (await FindAsync())!.ApprovalId);
    }

    // ---- the gate: consume-on-allow ----

    private sealed class AllowGate : IToolPermissionGate
    {
        public ValueTask<PermissionDecision> EvaluateAsync(ToolCallContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PermissionDecision.Allow("approved"));
    }

    private sealed class GrantSessions(Guid session, Func<ValueTask<ToolApprovalGrant?>> grant) : IPushSessionDirectory
    {
        public async ValueTask<PushSession?> FindAsync(PushGateRequest request, CancellationToken cancellationToken) =>
            new(session.ToString("D"), SessionTaint.Hard, ToolSessionRole.Actor, "external", await grant());
    }

    private static PermissionGatePushGate Gate(IPushSessionDirectory sessions, IPushApprovalGrants? grants) => new(new AllowGate(), sessions, grants);

    [Fact]
    public async Task An_approved_hard_tainted_push_proceeds_once_and_the_same_approval_cannot_push_again()
    {
        await ApprovedAsync();
        var grants = Grants();
        var gate = Gate(new GrantSessions(_session, async () => await grants.FindAsync(Push(), _session.ToString("D"), _ct)), grants);

        var first = await gate.EvaluateAsync(Push(), _ct);
        var second = await gate.EvaluateAsync(Push(), _ct);

        Assert.Equal(PushVerdict.Allow, first.Verdict);
        // The second look-up finds no grant (it was consumed), so the session carries none and the gate, which here always allows,
        // consumes nothing; the real gate then asks for approval again. The approval itself is gone.
        Assert.Equal(PushVerdict.Allow, second.Verdict);
        Assert.Null(await grants.FindAsync(Push(), _session.ToString("D"), _ct));
    }

    [Fact]
    public async Task Two_concurrent_pushes_on_one_approval_let_exactly_one_through()
    {
        await ApprovedAsync();
        var grants = Grants();
        var found = (await grants.FindAsync(Push(), _session.ToString("D"), _ct))!;
        var gate = Gate(new GrantSessions(_session, () => ValueTask.FromResult<ToolApprovalGrant?>(found)), grants);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () => await gate.EvaluateAsync(Push(), _ct), _ct)));

        Assert.Equal(1, results.Count(r => r.Verdict == PushVerdict.Allow));
        Assert.Equal(15, results.Count(r => r.Verdict == PushVerdict.Deny));
    }

    [Fact]
    public async Task An_allowed_push_whose_grant_cannot_be_spent_is_denied()
    {
        await ApprovedAsync();
        var found = (await FindAsync())!;
        var failing = Grants(new ThrowingStore(_store, replaceThrows: true));
        var sessions = new GrantSessions(_session, () => ValueTask.FromResult<ToolApprovalGrant?>(found));

        Assert.Equal(PushVerdict.Deny, (await Gate(sessions, failing).EvaluateAsync(Push(), _ct)).Verdict);
        Assert.Equal(PushVerdict.Deny, (await Gate(sessions, null).EvaluateAsync(Push(), _ct)).Verdict);
    }

    [Fact]
    public async Task An_allowed_push_with_no_grant_in_the_session_consumes_nothing()
    {
        var row = await ApprovedAsync();
        var sessions = new GrantSessions(_session, () => ValueTask.FromResult<ToolApprovalGrant?>(null));

        var decision = await Gate(sessions, Grants()).EvaluateAsync(Push(), _ct);

        Assert.Equal(PushVerdict.Allow, decision.Verdict);
        Assert.Equal(ApprovalStatus.Approved, (await _store.GetAsync(row.Id, _ct))!.Status);
    }

    [Fact]
    public async Task The_null_source_never_has_a_grant_and_never_consumes()
    {
        Assert.Null(await NoPushApprovalGrants.Instance.FindAsync(Push(), _session.ToString("D"), _ct));
        Assert.False(await NoPushApprovalGrants.Instance.ConsumeAsync(new ToolApprovalGrant(Guid.NewGuid(), new byte[32], _now.AddHours(1)), _ct));
    }
}
