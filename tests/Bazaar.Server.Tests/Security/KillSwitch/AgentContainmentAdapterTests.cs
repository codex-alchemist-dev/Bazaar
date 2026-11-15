using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.Server.Security.Approvals;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Quarantine;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Security.KillSwitch;

/// <summary>Pauser that appends to a shared order list, so tests can check the call order and inject a failure.</summary>
internal sealed class OrderedVmPauser(List<string> order) : IVmPauser
{
    /// <summary>When set, <see cref="PauseAsync"/> throws, to test a failing pause.</summary>
    public bool Fail { get; set; }

    /// <summary>When set, <see cref="ResumeAsync"/> throws, to test a failing VM resume.</summary>
    public bool FailResume { get; set; }

    public Task PauseAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("pause:" + agentId);
        return Fail ? Task.FromException(new InvalidOperationException("pause failed")) : Task.CompletedTask;
    }

    public Task ResumeAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("vm-resume:" + agentId);
        return FailResume ? Task.FromException(new InvalidOperationException("vm resume failed")) : Task.CompletedTask;
    }
}

/// <summary>Revoker that appends to a shared order list, so tests can check the call order.</summary>
internal sealed class OrderedTokenRevoker(List<string> order) : IAgentTokenRevoker
{
    public Task RevokeAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("revoke:" + agentId);
        return Task.CompletedTask;
    }

    public Task<TokenResumeResult> ResumeAsync(string agentId, CancellationToken ct = default)
    {
        order.Add("token-resume:" + agentId);
        return Task.FromResult(TokenResumeResult.NotReversible);
    }
}

public class AgentContainmentAdapterTests
{
    private static readonly string[] _containOrder = ["revoke:agent-a", "pause:agent-a"];

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly ECDsa _ownerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _revokedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<string> _order = [];
    private readonly OrderedVmPauser _pauser;
    private readonly OrderedTokenRevoker _revoker;
    private readonly RecordingAuditLog _log = new();
    private readonly AgentContainmentAdapter _adapter;

    public AgentContainmentAdapterTests()
    {
        _pauser = new OrderedVmPauser(_order);
        _revoker = new OrderedTokenRevoker(_order);
        var owners = new InMemoryOwnerKeyRegistry();
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", _ownerKey.ExportSubjectPublicKeyInfo()));
        owners.Register(new OwnerDeviceKey("owner-1", "dev-revoked", _revokedKey.ExportSubjectPublicKeyInfo(), Revoked: true));
        _adapter = new AgentContainmentAdapter(_pauser, _revoker, owners, _log, new InMemoryNonceStore(_clock), _clock);
    }

    [Fact]
    public async Task Contain_revokes_tokens_then_pauses_the_vms_once_each()
    {
        await _adapter.ContainAsync("agent-a", "owner-1", TestContext.Current.CancellationToken);

        Assert.Equal(_containOrder, _order);
    }

    [Fact]
    public async Task Contain_is_safe_to_repeat()
    {
        await _adapter.ContainAsync("agent-a", "owner-1", TestContext.Current.CancellationToken);
        await _adapter.ContainAsync("agent-a", "owner-1", TestContext.Current.CancellationToken);

        Assert.Equal(4, _order.Count);
    }

    [Fact]
    public async Task A_failing_pause_surfaces_after_the_tokens_are_revoked()
    {
        _pauser.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _adapter.ContainAsync("agent-a", "owner-1", TestContext.Current.CancellationToken));

        Assert.Equal(_containOrder, _order);
    }

    private static string Step(AuditEntry e) => JsonNode.Parse(e.Payload)!["step"]!.GetValue<string>();

    private static byte[] SignWith(ECDsa key, string agentId, string deviceId, ReleaseChallenge c) =>
        key.SignData(
            ApprovalSignature.ReleaseMessage(agentId, deviceId, c.Nonce, c.ExpiresAt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private async Task<(ReleaseChallenge Challenge, ContainmentReleaseRequest Request)> SignedAsync(
        string agent = "agent-a", string device = "dev-owner", ECDsa? key = null)
    {
        var challenge = await _adapter.IssueReleaseChallengeAsync(agent, device, TestContext.Current.CancellationToken);
        Assert.NotNull(challenge);
        var request = new ContainmentReleaseRequest(agent, device, challenge.Nonce, challenge.ExpiresAt, SignWith(key ?? _ownerKey, agent, device, challenge));
        return (challenge, request);
    }

    private async Task AssertDeniedAsync(ContainmentReleaseRequest request, string agent = "agent-a")
    {
        _order.Clear();
        _log.Reset();
        var result = await _adapter.ReleaseAsync(agent, request, TestContext.Current.CancellationToken);

        Assert.Equal(ContainmentReleaseOutcome.Denied, result.Outcome);
        Assert.Empty(_order);
        Assert.Equal("denied", Step(Assert.Single(_log.Entries)));
    }

    [Fact]
    public async Task A_valid_signed_request_releases_vms_then_tokens_with_one_audit_record_per_step()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (_, request) = await SignedAsync();
        _order.Clear();
        _log.Reset();

        var result = await _adapter.ReleaseAsync("agent-a", request, ct);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.False(result.TokensRestored);
        Assert.Equal(["vm-resume:agent-a", "token-resume:agent-a"], _order);
        Assert.Equal(["vm_resumed", "tokens_not_reversible"], _log.Entries.Select(Step));
        Assert.All(_log.Entries, e =>
        {
            Assert.Equal(AgentContainmentAdapter.ReleaseKind, e.Kind);
            Assert.Equal("owner-1", e.Actor);
        });
    }

    [Fact]
    public async Task The_challenge_nonce_is_128_bits_random_and_expires_in_the_future()
    {
        var a = await _adapter.IssueReleaseChallengeAsync("agent-a", "dev-owner", TestContext.Current.CancellationToken);
        var b = await _adapter.IssueReleaseChallengeAsync("agent-a", "dev-owner", TestContext.Current.CancellationToken);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(16, a.Nonce.Length);
        Assert.NotEqual(a.Nonce, b.Nonce);
        Assert.True(a.ExpiresAt > _clock.GetUtcNow());
    }

    [Theory]
    [InlineData("dev-unknown")]
    [InlineData("dev-revoked")]
    [InlineData("agent-a")]
    public async Task A_challenge_is_not_issued_to_an_unregistered_or_revoked_device(string device)
    {
        var challenge = await _adapter.IssueReleaseChallengeAsync("agent-a", device, TestContext.Current.CancellationToken);

        Assert.Null(challenge);
        Assert.Equal("challenge_denied", Step(Assert.Single(_log.Entries)));
    }

    [Fact]
    public async Task A_bad_signature_is_denied_audited_and_resumes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (challenge, _) = await SignedAsync();
        var forged = new ContainmentReleaseRequest("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt, SignWith(_otherKey, "agent-a", "dev-owner", challenge));

        await AssertDeniedAsync(forged);
    }

    [Fact]
    public async Task A_forged_request_does_not_burn_the_owners_nonce()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (challenge, genuine) = await SignedAsync();
        await _adapter.ReleaseAsync("agent-a", genuine with { Signature = new byte[64] }, ct);

        var result = await _adapter.ReleaseAsync("agent-a", genuine, ct);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
        Assert.NotNull(challenge);
    }

    [Fact]
    public async Task A_tampered_expiry_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (_, request) = await SignedAsync();

        await AssertDeniedAsync(request with { ExpiresAt = request.ExpiresAt.AddHours(1) });
    }

    [Fact]
    public async Task A_replayed_nonce_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (_, request) = await SignedAsync();
        Assert.Equal(ContainmentReleaseOutcome.Released, (await _adapter.ReleaseAsync("agent-a", request, ct)).Outcome);
        await _adapter.ContainAsync("agent-a", "owner-1", ct);

        await AssertDeniedAsync(request);
    }

    [Fact]
    public async Task An_expired_nonce_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (_, request) = await SignedAsync();
        _clock.Advance(AgentContainmentAdapter.ChallengeLifetime + TimeSpan.FromSeconds(1));

        await AssertDeniedAsync(request);
    }

    [Fact]
    public async Task A_nonce_that_was_never_issued_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var challenge = new ReleaseChallenge(RandomNumberGenerator.GetBytes(16), _clock.GetUtcNow().AddMinutes(1));
        var request = new ContainmentReleaseRequest("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt, SignWith(_ownerKey, "agent-a", "dev-owner", challenge));

        await AssertDeniedAsync(request);
    }

    [Theory]
    [InlineData("dev-unknown")]
    [InlineData("dev-revoked")]
    public async Task An_unregistered_or_revoked_device_is_denied(string device)
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (challenge, _) = await SignedAsync();
        var key = device == "dev-revoked" ? _revokedKey : _otherKey;
        var request = new ContainmentReleaseRequest("agent-a", device, challenge.Nonce, challenge.ExpiresAt, SignWith(key, "agent-a", device, challenge));

        await AssertDeniedAsync(request);
    }

    [Fact]
    public async Task A_device_revoked_after_the_challenge_is_denied()
    {
        var ct = TestContext.Current.CancellationToken;
        var owners = new InMemoryOwnerKeyRegistry();
        var spki = _ownerKey.ExportSubjectPublicKeyInfo();
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", spki));
        var adapter = new AgentContainmentAdapter(_pauser, _revoker, owners, _log, new InMemoryNonceStore(_clock), _clock);
        await adapter.ContainAsync("agent-a", "owner-1", ct);
        var challenge = (await adapter.IssueReleaseChallengeAsync("agent-a", "dev-owner", ct))!;
        owners.Register(new OwnerDeviceKey("owner-1", "dev-owner", spki, Revoked: true));
        var request = new ContainmentReleaseRequest("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt, SignWith(_ownerKey, "agent-a", "dev-owner", challenge));
        _order.Clear();

        var result = await adapter.ReleaseAsync("agent-a", request, ct);

        Assert.Equal(ContainmentReleaseOutcome.Denied, result.Outcome);
        Assert.Empty(_order);
    }

    [Fact]
    public async Task A_nonce_issued_for_one_agent_cannot_release_another()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        await _adapter.ContainAsync("agent-b", "owner-1", ct);
        var (challenge, _) = await SignedAsync("agent-a");
        // The owner key signs a request for agent-b over agent-a's nonce.
        var request = new ContainmentReleaseRequest("agent-b", "dev-owner", challenge.Nonce, challenge.ExpiresAt, SignWith(_ownerKey, "agent-b", "dev-owner", challenge));

        await AssertDeniedAsync(request, "agent-b");
    }

    [Fact]
    public async Task A_request_signed_for_one_agent_cannot_release_another()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        await _adapter.ContainAsync("agent-b", "owner-1", ct);
        var (_, request) = await SignedAsync("agent-a");

        await AssertDeniedAsync(request, "agent-b");
    }

    [Fact]
    public async Task A_denied_release_leaves_the_agent_releasable_by_the_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (challenge, _) = await SignedAsync();
        await _adapter.ReleaseAsync("agent-a", new ContainmentReleaseRequest("agent-a", "dev-unknown", challenge.Nonce, challenge.ExpiresAt, new byte[64]), ct);
        var (_, request) = await SignedAsync();

        var result = await _adapter.ReleaseAsync("agent-a", request, ct);

        Assert.Equal(ContainmentReleaseOutcome.Released, result.Outcome);
    }

    [Fact]
    public async Task Releasing_an_agent_that_was_never_contained_is_a_no_op_that_still_consumes_the_nonce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, request) = await SignedAsync();
        _log.Reset();

        var result = await _adapter.ReleaseAsync("agent-a", request, ct);

        Assert.Equal(ContainmentReleaseOutcome.NotContained, result.Outcome);
        Assert.Empty(_order);
        Assert.Equal("not_contained", Step(Assert.Single(_log.Entries)));
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        await AssertDeniedAsync(request);
    }

    [Fact]
    public async Task A_failing_vm_resume_stops_the_token_resume_is_audited_and_the_nonce_is_spent()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (_, request) = await SignedAsync();
        _order.Clear();
        _log.Reset();
        _pauser.FailResume = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _adapter.ReleaseAsync("agent-a", request, ct));

        Assert.Equal(["vm-resume:agent-a"], _order);
        Assert.Equal("vm_resume_failed", Step(Assert.Single(_log.Entries)));

        // The agent is still contained, but the old nonce is spent: the owner needs a fresh signed request.
        _pauser.FailResume = false;
        await AssertDeniedAsync(request);
        var (_, fresh) = await SignedAsync();
        Assert.Equal(ContainmentReleaseOutcome.Released, (await _adapter.ReleaseAsync("agent-a", fresh, ct)).Outcome);
    }

    [Fact]
    public async Task A_second_release_after_a_completed_release_is_denied_by_the_spent_nonce()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (_, request) = await SignedAsync();
        await _adapter.ReleaseAsync("agent-a", request, ct);

        await AssertDeniedAsync(request);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Contain_issue_and_release_reject_a_blank_agent_id(string agentId)
    {
        var request = ReleaseRequests.Unsigned("agent-a", "dev-owner");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.ContainAsync(agentId, "owner-1", TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.IssueReleaseChallengeAsync(agentId, "dev-owner", TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _adapter.ReleaseAsync(agentId, request, TestContext.Current.CancellationToken));
        Assert.Empty(_order);
    }

    [Fact]
    public async Task A_malformed_request_is_denied_not_thrown()
    {
        await _adapter.ContainAsync("agent-a", "owner-1", TestContext.Current.CancellationToken);

        await AssertDeniedAsync(new ContainmentReleaseRequest("agent-a", "dev-owner", [1, 2, 3], _clock.GetUtcNow(), new byte[64]));
        await AssertDeniedAsync(new ContainmentReleaseRequest("agent-a", "dev owner\nx", new byte[16], _clock.GetUtcNow(), new byte[64]));
    }

    [Fact]
    public async Task An_approval_signature_cannot_be_replayed_as_a_release_signature()
    {
        var ct = TestContext.Current.CancellationToken;
        await _adapter.ContainAsync("agent-a", "owner-1", ct);
        var (challenge, _) = await SignedAsync();
        var approvalSig = _ownerKey.SignData(
            ApprovalSignature.Message(Guid.Empty, ApprovalDecision.Approve, new byte[32], challenge.ExpiresAt, "dev-owner"),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        await AssertDeniedAsync(new ContainmentReleaseRequest("agent-a", "dev-owner", challenge.Nonce, challenge.ExpiresAt, approvalSig));
    }

    [Fact]
    public async Task An_agent_stop_through_the_engine_contains_the_agent_once()
    {
        var engine = new KillSwitchEngine(new KillSwitchAuditAdapter(_log), _adapter, _clock);

        await engine.EngageAgentAsync("owner-1", "agent-a", "Suspicious output", TestContext.Current.CancellationToken);

        Assert.Equal(_containOrder, _order);
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task An_engine_resume_with_an_unsigned_request_is_refused_and_the_agent_stays_stopped()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new KillSwitchEngine(new KillSwitchAuditAdapter(_log), _adapter, _clock);
        await engine.EngageAgentAsync("owner-1", "agent-a", "Stop", ct);
        _order.Clear();

        // A registered device id alone, with no valid signature, does not resume.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => engine.ResumeAgentAsync("agent-a", "Back", ReleaseRequests.Unsigned("agent-a", "dev-owner"), ct));

        Assert.Empty(_order);
        Assert.False(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }

    [Fact]
    public async Task An_engine_resume_with_a_signed_request_releases_and_lifts_the_stop()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = new KillSwitchEngine(new KillSwitchAuditAdapter(_log), _adapter, _clock);
        await engine.EngageAgentAsync("owner-1", "agent-a", "Stop", ct);
        var (_, request) = await SignedAsync();
        _order.Clear();

        await engine.ResumeAgentAsync("agent-a", "Back", request, ct);

        Assert.Equal(["vm-resume:agent-a", "token-resume:agent-a"], _order);
        Assert.True(engine.Evaluate("agent-a", ToolEffect.Read).Allowed);
    }
}
