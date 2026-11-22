using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Client.GitGui.Merge;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Tests;

internal sealed class FakeBiometric(BiometricOutcome outcome = BiometricOutcome.Succeeded) : IBiometricPrompt
{
    public List<BiometricRequest> Prompts { get; } = [];

    public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken)
    {
        Prompts.Add(request);
        return ValueTask.FromResult(outcome);
    }
}

/// <summary>A real P-256 software key, for tests only.</summary>
internal sealed class TestSigner : IDeviceApprovalSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string DeviceId => "test-device";

    public int Signed { get; private set; }

    public Exception? Failure { get; set; }

    public byte[] PublicKeySpki => _key.ExportSubjectPublicKeyInfo();

    public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Signed++;
        return ValueTask.FromResult(_key.SignData(canonicalPayload.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    public void Dispose() => _key.Dispose();
}

internal sealed class TestIdentity : IOwnerMergeIdentity
{
    public static readonly Guid Owner = Guid.Parse("11111111-2222-3333-4444-555555555555");

    public Guid HumanId => Owner;

    public string SignOff => "Signed-off-by: Owner <owner@example.test>";
}

internal sealed class OneKeyRegistry(TestSigner signer) : IOwnerKeyRegistry
{
    public Task<OwnerDeviceKey?> FindAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult<OwnerDeviceKey?>(deviceId == signer.DeviceId ? new OwnerDeviceKey(TestIdentity.Owner.ToString("D"), deviceId, signer.PublicKeySpki) : null);
}

public sealed class MergeSigningTests
{
    private static readonly MergeApprovalRequest _request = new("github", "acme/widgets", 7, Fixtures.Head1.ToUpperInvariant(), "Title (#7)");

    /// <summary>Rebuilds the server's payload from the wire DTO, as GitMergeRoutes does, so the verifier sees what the server would.</summary>
    private static OwnerPayload ServerPayload(GitOwnerApprovalDto a) => new(
        a.Action, a.ApprovalId, Convert.FromBase64String(a.ArgsHashBase64), a.HeadSha, a.HumanId, a.DeviceId,
        a.IssuedAt, a.ExpiresAt, Convert.FromBase64String(a.NonceBase64));

    [Fact]
    public async Task Approval_round_trips_against_the_real_verifier()
    {
        using var signer = new TestSigner();
        var approver = new DeviceOwnerMergeApprover(new FakeBiometric(), signer, new TestIdentity());

        var result = await approver.ApproveAsync(_request, TestContext.Current.CancellationToken);

        Assert.Equal(MergeApprovalOutcome.Approved, result.Outcome);
        var dto = result.Merge!;
        var verifier = new OwnerSignatureVerifier(new OneKeyRegistry(signer), new InMemoryOwnerNonceStore());
        var verdict = await verifier.VerifyAsync(
            ServerPayload(dto.Approval), Convert.FromBase64String(dto.Approval.SignatureBase64), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        Assert.Equal(OwnerSignatureVerdict.Valid, verdict);
    }

    [Fact]
    public async Task Payload_binds_the_head_action_args_hash_and_expiry_the_gate_checks()
    {
        using var signer = new TestSigner();
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var approver = new DeviceOwnerMergeApprover(new FakeBiometric(), signer, new TestIdentity(), clock);

        var dto = (await approver.ApproveAsync(_request, TestContext.Current.CancellationToken)).Merge!;

        Assert.Equal(Fixtures.Head1, dto.HeadSha);
        Assert.Equal(Fixtures.Head1, dto.Approval.HeadSha);
        Assert.Equal("pr.merge", dto.Approval.Action);
        var expected = GitMergeArgsHash.Compute("github", "acme/widgets", 7, Fixtures.Head1, "Title (#7)", new TestIdentity().SignOff);
        Assert.Equal(Convert.ToBase64String(expected), dto.Approval.ArgsHashBase64);
        Assert.Equal(clock.GetUtcNow(), dto.Approval.IssuedAt);
        Assert.Equal(clock.GetUtcNow() + DeviceOwnerMergeApprover.DefaultLifetime, dto.Approval.ExpiresAt);
        Assert.Equal(OwnerSignedPayload.NonceLength, Convert.FromBase64String(dto.Approval.NonceBase64).Length);
    }

    [Fact]
    public async Task A_changed_merge_field_voids_the_signature()
    {
        using var signer = new TestSigner();
        var dto = (await new DeviceOwnerMergeApprover(new FakeBiometric(), signer, new TestIdentity())
            .ApproveAsync(_request, TestContext.Current.CancellationToken)).Merge!;
        var verifier = new OwnerSignatureVerifier(new OneKeyRegistry(signer), new InMemoryOwnerNonceStore());

        var tampered = ServerPayload(dto.Approval) with { HeadSha = Fixtures.Head2 };
        var verdict = await verifier.VerifyAsync(tampered, Convert.FromBase64String(dto.Approval.SignatureBase64), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal(OwnerSignatureVerdict.BadSignature, verdict);
    }

    [Fact]
    public async Task Each_approval_uses_a_fresh_nonce_and_id()
    {
        using var signer = new TestSigner();
        var approver = new DeviceOwnerMergeApprover(new FakeBiometric(), signer, new TestIdentity());

        var a = (await approver.ApproveAsync(_request, TestContext.Current.CancellationToken)).Merge!.Approval;
        var b = (await approver.ApproveAsync(_request, TestContext.Current.CancellationToken)).Merge!.Approval;

        Assert.NotEqual(a.NonceBase64, b.NonceBase64);
        Assert.NotEqual(a.ApprovalId, b.ApprovalId);
    }

    [Theory]
    [InlineData(BiometricOutcome.Cancelled, MergeApprovalOutcome.Cancelled)]
    [InlineData(BiometricOutcome.Failed, MergeApprovalOutcome.Cancelled)]
    [InlineData(BiometricOutcome.Unavailable, MergeApprovalOutcome.Unavailable)]
    public async Task A_failed_presence_check_signs_nothing(BiometricOutcome biometric, MergeApprovalOutcome expected)
    {
        using var signer = new TestSigner();
        var approver = new DeviceOwnerMergeApprover(new FakeBiometric(biometric), signer, new TestIdentity());

        var result = await approver.ApproveAsync(_request, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Merge);
        Assert.Equal(0, signer.Signed);
    }

    [Fact]
    public async Task The_biometric_prompt_names_the_exact_merge()
    {
        using var signer = new TestSigner();
        var bio = new FakeBiometric();
        await new DeviceOwnerMergeApprover(bio, signer, new TestIdentity()).ApproveAsync(_request, TestContext.Current.CancellationToken);

        var prompt = Assert.Single(bio.Prompts);
        Assert.Equal(Convert.ToHexStringLower(GitMergeArgsHash.Compute("github", "acme/widgets", 7, Fixtures.Head1, "Title (#7)", new TestIdentity().SignOff)), prompt.ContentHash);
        Assert.Contains("#7", prompt.Subtitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_signer_failure_is_reported_without_a_request()
    {
        using var signer = new TestSigner { Failure = new CryptographicException("no key") };
        var result = await new DeviceOwnerMergeApprover(new FakeBiometric(), signer, new TestIdentity()).ApproveAsync(_request, TestContext.Current.CancellationToken);

        Assert.Equal(MergeApprovalOutcome.Failed, result.Outcome);
        Assert.Null(result.Merge);
    }

    [Fact]
    public void Commit_title_is_one_capped_line_with_the_number()
    {
        Assert.Equal("Fix it (#9)", DeviceOwnerMergeApprover.CommitTitleFor("Fix\r\nit", 9));
        Assert.Equal(200 + " (#9)".Length, DeviceOwnerMergeApprover.CommitTitleFor(new string('x', 500), 9).Length);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
