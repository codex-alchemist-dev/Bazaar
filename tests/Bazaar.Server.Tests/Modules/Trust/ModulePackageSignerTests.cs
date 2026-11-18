using System.Security.Cryptography;
using Bazaar.Contracts.Modules;
using Bazaar.Install.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Trust;

public class ModulePackageSignerTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
    private const string _keyId = "release-key-test";
    private const string _digest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static ModulePackage Unsigned(string digest = _digest) =>
        new("postgresql", "18.7.0", new HashSet<string>(StringComparer.Ordinal) { "fs.dataRoot" }, digest, _keyId,
            new HostRequirements(1, ["windows", "linux"], ["x64"], []), []);

    private static (EcdsaModuleSignatureVerifier Verifier, ECDsa Key) Setup(bool revoked = false)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey(_keyId, "publisher-test", key, _now.AddDays(-1), _now.AddYears(1), revoked));
        return (new EcdsaModuleSignatureVerifier(store, new FakeTimeProvider(_now)), key);
    }

    [Fact]
    public void Signed_package_verifies_under_its_key()
    {
        var (verifier, key) = Setup();

        var signed = TestPackageSigner.Sign(Unsigned(), key);

        Assert.Equal(64, signed.Signature.Length);
        Assert.Equal(ModuleSignatureStatus.Valid, verifier.Verify(signed));
    }

    [Fact]
    public void Signing_keeps_every_other_field()
    {
        var (_, key) = Setup();
        var unsigned = Unsigned();

        var signed = TestPackageSigner.Sign(unsigned, key);

        Assert.Equal(unsigned.ModuleId, signed.ModuleId);
        Assert.Equal(unsigned.Version, signed.Version);
        Assert.Equal(unsigned.ContentDigestSha256Hex, signed.ContentDigestSha256Hex);
        Assert.Equal(unsigned.SignerKeyId, signed.SignerKeyId);
        Assert.Equal(unsigned.DeclaredPermissions, signed.DeclaredPermissions);
    }

    [Fact]
    public void A_changed_digest_after_signing_is_a_bad_signature()
    {
        var (verifier, key) = Setup();
        var signed = TestPackageSigner.Sign(Unsigned(), key);

        var tampered = signed with { ContentDigestSha256Hex = new string('b', 64) };

        Assert.Equal(ModuleSignatureStatus.BadSignature, verifier.Verify(tampered));
    }

    [Fact]
    public void A_revoked_key_never_verifies_what_it_signed()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey(_keyId, "publisher-test", key, _now.AddDays(-1), _now.AddYears(1), Revoked: true));
        var verifier = new EcdsaModuleSignatureVerifier(store, new FakeTimeProvider(_now));

        var signed = TestPackageSigner.Sign(Unsigned(), key);

        Assert.NotEqual(ModuleSignatureStatus.Valid, verifier.Verify(signed));
    }

    [Fact]
    public void Non_P256_key_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<ArgumentException>(() => TestPackageSigner.Sign(Unsigned(), key));
    }
}

/// <summary>
/// The test's own reference signer. The production signer lives in packaging/postgres/source/SignBundle.cs, outside
/// Bazaar.Server, because the server only verifies (doc 49 section 3). This copy exists so the verifier tests can produce
/// packages to check. It is test code and never ships.
/// </summary>
internal static class TestPackageSigner
{
    public static ModulePackage Sign(ModulePackage package, ECDsa privateKey)
    {
        if (privateKey.KeySize != 256 || privateKey.ExportParameters(includePrivateParameters: false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
        {
            throw new ArgumentException("The module signing key must be ECDSA P-256.", nameof(privateKey));
        }

        var message = ModuleCanonicalForm.Build(package);
        var signature = privateKey.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return package with { Signature = signature };
    }
}
