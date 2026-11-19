using System.Security.Cryptography;
using System.Text;
using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Tests.Runtime;

public sealed class RuntimeSignatureCheckerTests
{
    private static readonly byte[] _message = Encoding.UTF8.GetBytes("""{"slotId":"slot-a"}""");

    [Fact]
    public void ValidSignatureUnderAPinnedKeyIsValid()
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());
        string signature = RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, _message);

        Assert.Equal(
            RuntimeSignatureStatus.Valid,
            checker.Check(_message, RuntimeTestSigning.KeyId, signature, RuntimeTestSigning.Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingSignatureIsUnsigned(string? signature)
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());

        Assert.Equal(
            RuntimeSignatureStatus.NoSignature,
            checker.Check(_message, RuntimeTestSigning.KeyId, signature, RuntimeTestSigning.Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some-other-key")]
    public void KeyIdNotInTheTrustRootIsUnknown(string? keyId)
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());
        string signature = RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, _message);

        Assert.Equal(
            RuntimeSignatureStatus.UnknownKey,
            checker.Check(_message, keyId, signature, RuntimeTestSigning.Now));
    }

    [Fact]
    public void KeyNotYetValidIsOutsideValidity()
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot(notBefore: RuntimeTestSigning.Now.AddMinutes(1)));
        string signature = RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, _message);

        Assert.Equal(
            RuntimeSignatureStatus.KeyOutsideValidity,
            checker.Check(_message, RuntimeTestSigning.KeyId, signature, RuntimeTestSigning.Now));
    }

    [Fact]
    public void KeyExpiresAtNotAfterSoTheWindowIsHalfOpen()
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot(notAfter: RuntimeTestSigning.Now));
        string signature = RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, _message);

        Assert.Equal(
            RuntimeSignatureStatus.KeyOutsideValidity,
            checker.Check(_message, RuntimeTestSigning.KeyId, signature, RuntimeTestSigning.Now));
    }

    [Fact]
    public void SignatureByAnotherKeyUnderTheTrustedKeyIdIsBad()
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());
        string signature = RuntimeTestSigning.Sign(RuntimeTestSigning.OtherKey, _message);

        Assert.Equal(
            RuntimeSignatureStatus.BadSignature,
            checker.Check(_message, RuntimeTestSigning.KeyId, signature, RuntimeTestSigning.Now));
    }

    [Fact]
    public void ChangedMessageIsABadSignature()
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());
        string signature = RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, _message);
        byte[] changed = Encoding.UTF8.GetBytes("""{"slotId":"slot-b"}""");

        Assert.Equal(
            RuntimeSignatureStatus.BadSignature,
            checker.Check(changed, RuntimeTestSigning.KeyId, signature, RuntimeTestSigning.Now));
    }

    [Fact]
    public void FlippedSignatureBitIsABadSignature()
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());
        byte[] raw = Convert.FromBase64String(RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, _message));
        raw[^1] ^= 0x01;

        Assert.Equal(
            RuntimeSignatureStatus.BadSignature,
            checker.Check(_message, RuntimeTestSigning.KeyId, Convert.ToBase64String(raw), RuntimeTestSigning.Now));
    }

    [Fact]
    public void SignatureThatIsNotBase64IsABadSignature()
    {
        RuntimeSignatureChecker checker = new(RuntimeTestSigning.TrustRoot());

        Assert.Equal(
            RuntimeSignatureStatus.BadSignature,
            checker.Check(_message, RuntimeTestSigning.KeyId, "@@not base64@@", RuntimeTestSigning.Now));
    }

    [Fact]
    public void KeyNamingAnUnknownAlgorithmIsUnsupported()
    {
        TrustedKey key = new(
            RuntimeTestSigning.KeyId,
            "ed25519-not-built",
            RuntimeTestSigning.PrimaryKey.ExportSubjectPublicKeyInfo(),
            RuntimeTestSigning.Now.AddDays(-1),
            RuntimeTestSigning.Now.AddDays(1));
        RuntimeSignatureChecker checker = new(new RuntimeTrustRoot([key]));
        string signature = RuntimeTestSigning.Sign(RuntimeTestSigning.PrimaryKey, _message);

        Assert.Equal(
            RuntimeSignatureStatus.UnsupportedAlgorithm,
            checker.Check(_message, RuntimeTestSigning.KeyId, signature, RuntimeTestSigning.Now));
    }

    [Fact]
    public void RsaVerifierRefusesKeysBelowTheMinimumSize()
    {
        using RSA small = RSA.Create(1024);
        RsaPkcs1Sha256Verifier verifier = new();
        byte[] signature = small.SignData(_message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.False(verifier.Verify(_message, signature, small.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void RsaVerifierReturnsFalseForGarbageKeyBytes()
    {
        RsaPkcs1Sha256Verifier verifier = new();
        byte[] signature = RuntimeTestSigning.PrimaryKey.SignData(_message, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        Assert.False(verifier.Verify(_message, signature, [0x01, 0x02, 0x03]));
    }

    [Fact]
    public void TrustRootRejectsARepeatedKeyId()
    {
        TrustedKey first = KeyFor("release-a");
        TrustedKey second = KeyFor("release-a");

        Assert.Throws<ArgumentException>(() => new RuntimeTrustRoot([first, second]));
    }

    [Fact]
    public void TrustRootRejectsAnEmptyValidityWindow()
    {
        DateTimeOffset now = RuntimeTestSigning.Now;

        Assert.Throws<ArgumentException>(() => new RuntimeTrustRoot([
            new TrustedKey("release-a", RsaPkcs1Sha256Verifier.AlgorithmName, [1], now, now),
        ]));
    }

    [Fact]
    public void TwoVerifiersForOneAlgorithmAreRejected()
    {
        RuntimeTrustRoot trust = RuntimeTestSigning.TrustRoot();

        Assert.Throws<ArgumentException>(() => new RuntimeSignatureChecker(
            trust,
            [new RsaPkcs1Sha256Verifier(), new RsaPkcs1Sha256Verifier()]));
    }

    private static TrustedKey KeyFor(string keyId) => new(
        keyId,
        RsaPkcs1Sha256Verifier.AlgorithmName,
        RuntimeTestSigning.PrimaryKey.ExportSubjectPublicKeyInfo(),
        RuntimeTestSigning.Now.AddDays(-1),
        RuntimeTestSigning.Now.AddDays(1));
}
