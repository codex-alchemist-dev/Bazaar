using Bazaar.Kodo.Manifest;
using Bazaar.Kodo.Runtime;
using Bazaar.Kodo.Tests.Runtime;

namespace Bazaar.Kodo.Tests.Manifest;

public sealed class ModuleManifestLoaderTests
{
    private static readonly IReadOnlySet<string> _allApproved = new HashSet<string>(StringComparer.Ordinal) { "db.read:gc", "db.write:gc", "kodo.observe" };

    [Fact]
    public void SignedValidManifestNeedsOwnerApprovalForEveryPermission()
    {
        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(ManifestTestData.Valid());

        Assert.Equal(ModuleLoadOutcome.NeedsOwnerApproval, decision.Outcome);
        Assert.Equal("gc", decision.ModuleId);
        Assert.Empty(decision.Problems);
        Assert.Equal(["db.read:gc", "db.write:gc", "kodo.observe"], decision.PermissionsNeedingApproval);
        Assert.NotNull(decision.ManifestDigest);
        Assert.Equal(64, decision.ManifestDigest!.Length);
    }

    [Fact]
    public void KodoObserveIsNeverAutoGranted()
    {
        ModuleManifest manifest = ManifestTestData.Valid() with { Permissions = ["kodo.observe"] };

        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(manifest);

        Assert.Equal(ModuleLoadOutcome.NeedsOwnerApproval, decision.Outcome);
        Assert.Equal(["kodo.observe"], decision.PermissionsNeedingApproval);
    }

    [Fact]
    public void ApprovedPermissionsLeaveNothingPending()
    {
        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(ManifestTestData.Valid(), _allApproved);

        Assert.Equal(ModuleLoadOutcome.Loadable, decision.Outcome);
        Assert.Empty(decision.PermissionsNeedingApproval);
    }

    [Fact]
    public void OnlyTheUnapprovedPermissionsArePending()
    {
        IReadOnlySet<string> approved = new HashSet<string>(StringComparer.Ordinal) { "db.read:gc" };

        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(ManifestTestData.Valid(), approved);

        Assert.Equal(ModuleLoadOutcome.NeedsOwnerApproval, decision.Outcome);
        Assert.Equal(["db.write:gc", "kodo.observe"], decision.PermissionsNeedingApproval);
    }

    [Fact]
    public void ManifestWithNoPermissionsIsLoadable()
    {
        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(ManifestTestData.Valid() with { Permissions = [] });

        Assert.Equal(ModuleLoadOutcome.Loadable, decision.Outcome);
    }

    [Fact]
    public void DigestIsStableAndIgnoresTheSignatureBlock()
    {
        ModuleLoadDecision first = ManifestTestData.EvaluateSigned(ManifestTestData.Valid());
        ModuleLoadDecision second = ManifestTestData.EvaluateSigned(ManifestTestData.Valid());
        ModuleLoadDecision changed = ManifestTestData.EvaluateSigned(ManifestTestData.Valid() with { Version = "0.2.0" });

        Assert.Equal(first.ManifestDigest, second.ManifestDigest);
        Assert.NotEqual(first.ManifestDigest, changed.ManifestDigest);
    }

    [Fact]
    public void MissingHealthBlockIsRejectedEvenWhenSigned()
    {
        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(ManifestTestData.Valid() with { Health = null });

        Assert.Equal(ModuleLoadOutcome.Rejected, decision.Outcome);
        Assert.Contains(ModuleManifestCauses.HealthMissing, decision.Causes);
        Assert.Empty(decision.PermissionsNeedingApproval);
    }

    [Fact]
    public void UnknownProbeIsRejected()
    {
        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(ManifestTestData.Valid() with { Probes = ["gc.nope"] });

        Assert.Equal(ModuleLoadOutcome.Rejected, decision.Outcome);
        Assert.Equal([ModuleManifestCauses.ProbeUnknown], decision.Causes);
    }

    [Fact]
    public void SeveralProblemsAreAllReportedInOrder()
    {
        ModuleManifest broken = ManifestTestData.Valid() with { Id = "BAD", Health = null, Permissions = ["*"] };

        ModuleLoadDecision decision = ManifestTestData.EvaluateSigned(broken);

        Assert.Equal(
            [ModuleManifestCauses.IdInvalid, ModuleManifestCauses.HealthMissing, ModuleManifestCauses.PermissionInvalid],
            decision.Causes);
        Assert.Null(decision.ModuleId);
    }

    [Fact]
    public void UnsignedManifestIsRejected()
    {
        ModuleLoadDecision decision = ManifestTestData.Evaluate(ManifestTestData.Valid());

        Assert.Equal(ModuleLoadOutcome.Rejected, decision.Outcome);
        Assert.Equal([ModuleManifestCauses.SignatureMissing], decision.Causes);
    }

    [Fact]
    public void TamperedContentFailsTheSignature()
    {
        ModuleManifest signed = ManifestTestData.Sign(ManifestTestData.Valid());
        ModuleManifest tampered = signed with { Permissions = ["db.read:gc", "db.write:gc", "kodo.observe", "db.write:secrets"] };

        ModuleLoadDecision decision = ManifestTestData.Evaluate(tampered);

        Assert.Equal(ModuleLoadOutcome.Rejected, decision.Outcome);
        Assert.Equal([ModuleManifestCauses.SignatureInvalid], decision.Causes);
        Assert.Empty(decision.PermissionsNeedingApproval);
    }

    [Fact]
    public void UntrustedSigningKeyIsRejected()
    {
        ModuleManifest signed = ManifestTestData.Sign(ManifestTestData.Valid(), RuntimeTestSigning.OtherKey);

        ModuleLoadDecision decision = ManifestTestData.Evaluate(signed);

        Assert.Equal([ModuleManifestCauses.SignatureInvalid], decision.Causes);
    }

    [Fact]
    public void UnknownKeyIdIsRejected()
    {
        ModuleManifest signed = ManifestTestData.Sign(ManifestTestData.Valid(), keyId: "someone-else");

        Assert.Equal([ModuleManifestCauses.SignatureUnknownKey], ManifestTestData.Evaluate(signed).Causes);
    }

    [Fact]
    public void KeyOutsideItsWindowIsRejected()
    {
        RuntimeTrustRoot expired = RuntimeTestSigning.TrustRoot(
            notBefore: RuntimeTestSigning.Now.AddYears(-2),
            notAfter: RuntimeTestSigning.Now.AddYears(-1));
        byte[] bytes = ModuleManifestCodec.Encode(ManifestTestData.Sign(ManifestTestData.Valid()));

        ModuleLoadDecision decision = ManifestTestData.Loader(expired).Evaluate(bytes, RuntimeTestSigning.Now);

        Assert.Equal([ModuleManifestCauses.SignatureKeyOutsideValidity], decision.Causes);
    }

    [Fact]
    public void ManifestCannotChooseItsOwnAlgorithm()
    {
        ModuleManifest signed = ManifestTestData.Sign(ManifestTestData.Valid(), alg: "none");

        Assert.Equal([ModuleManifestCauses.SignatureAlgMismatch], ManifestTestData.Evaluate(signed).Causes);
    }

    [Fact]
    public void KeyWithoutAVerifierIsUnsupported()
    {
        RuntimeTrustRoot trust = new([
            new TrustedKey("odd", "ed25519", [1, 2, 3], RuntimeTestSigning.Now.AddDays(-1), RuntimeTestSigning.Now.AddDays(1)),
        ]);
        ModuleManifest manifest = ManifestTestData.Valid() with
        {
            Signature = new ModuleManifestSignature("odd", "ed25519", Convert.ToBase64String([9, 9])),
        };

        ModuleLoadDecision decision = ManifestTestData.Loader(trust).Evaluate(ModuleManifestCodec.Encode(manifest), RuntimeTestSigning.Now);

        Assert.Equal([ModuleManifestCauses.SignatureUnsupportedAlgorithm], decision.Causes);
    }

    [Fact]
    public void GarbageSignatureTextIsInvalidNotACrash()
    {
        ModuleManifest manifest = ManifestTestData.Valid() with
        {
            Signature = new ModuleManifestSignature(RuntimeTestSigning.KeyId, RsaPkcs1Sha256Verifier.AlgorithmName, "not base64 !!"),
        };

        Assert.Equal([ModuleManifestCauses.SignatureInvalid], ManifestTestData.Evaluate(manifest).Causes);
    }

    [Fact]
    public void ReorderedWhitespaceInStoredBytesStillVerifies()
    {
        ModuleManifest signed = ManifestTestData.Sign(ManifestTestData.Valid());
        string pretty = System.Text.Encoding.UTF8.GetString(ModuleManifestCodec.Encode(signed)).Replace(",", ",\n  ", StringComparison.Ordinal);

        ModuleLoadDecision decision = ManifestTestData.Loader().Evaluate(System.Text.Encoding.UTF8.GetBytes(pretty), RuntimeTestSigning.Now);

        Assert.Equal(ModuleLoadOutcome.NeedsOwnerApproval, decision.Outcome);
    }

    [Fact]
    public void DecodeFailuresBecomeRejectedDecisions()
    {
        ModuleManifestLoader loader = ManifestTestData.Loader();

        ModuleLoadDecision duplicate = loader.Evaluate("""{"id":"a","id":"b"}"""u8, RuntimeTestSigning.Now);
        ModuleLoadDecision unknown = loader.Evaluate("""{"id":"a","surprise":1}"""u8, RuntimeTestSigning.Now);
        ModuleLoadDecision huge = loader.Evaluate(new byte[ModuleManifestCodec.MaxBytes + 1], RuntimeTestSigning.Now);

        Assert.Equal([ModuleManifestCauses.DuplicateField], duplicate.Causes);
        Assert.Equal([ModuleManifestCauses.Malformed], unknown.Causes);
        Assert.Equal([ModuleManifestCauses.TooLarge], huge.Causes);
        Assert.All([duplicate, unknown, huge], d =>
        {
            Assert.Equal(ModuleLoadOutcome.Rejected, d.Outcome);
            Assert.Null(d.ManifestDigest);
            Assert.Null(d.ModuleId);
        });
    }

    [Fact]
    public void TwoVerifiersForOneAlgorithmAreRefused()
    {
        Assert.Throws<ArgumentException>(() => new ModuleManifestLoader(
            RuntimeTestSigning.TrustRoot(),
            ManifestTestData.Catalogue(),
            [new RsaPkcs1Sha256Verifier(), new RsaPkcs1Sha256Verifier()]));
    }
}
