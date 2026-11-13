using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Modules.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Trust;

public class ModuleSignatureVerifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private const string KeyId = "publisher-key-1";
    private const string Digest = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static ModulePackage Unsigned(
        IReadOnlySet<string>? permissions = null,
        string moduleId = "bazaar.weather",
        string version = "1.2.0",
        string digest = Digest,
        string keyId = KeyId) =>
        new(moduleId, version, permissions ?? new HashSet<string>(StringComparer.Ordinal) { "gc.post", "network:api.example" }, digest, keyId, []);

    private static ModulePackage Sign(ModulePackage unsigned, ECDsa signer)
    {
        var message = ModuleCanonicalForm.Build(unsigned);
        var signature = signer.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return unsigned with { Signature = signature };
    }

    private static (EcdsaModuleSignatureVerifier Verifier, ECDsa Key) Setup(string keyId = KeyId, bool revoked = false, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null, FakeTimeProvider? time = null)
    {
        var key = NewKey();
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey(
            keyId,
            "publisher-1",
            key,
            notBefore ?? Now.AddYears(-1),
            notAfter ?? Now.AddYears(1),
            revoked));
        return (new EcdsaModuleSignatureVerifier(store, time ?? new FakeTimeProvider(Now)), key);
    }

    [Fact]
    public void Valid_signature_verifies()
    {
        var (verifier, key) = Setup();
        var package = Sign(Unsigned(), key);

        Assert.Equal(ModuleSignatureStatus.Valid, verifier.Verify(package));
    }

    [Fact]
    public void Tampered_digest_is_a_bad_signature()
    {
        var (verifier, key) = Setup();
        var signed = Sign(Unsigned(), key);
        var tampered = signed with { ContentDigestSha256Hex = new string('a', 64) };

        Assert.Equal(ModuleSignatureStatus.BadSignature, verifier.Verify(tampered));
    }

    [Fact]
    public void Tampered_permissions_is_a_bad_signature()
    {
        var (verifier, key) = Setup();
        var signed = Sign(Unsigned(), key);
        var widened = signed with
        {
            DeclaredPermissions = new HashSet<string>(StringComparer.Ordinal) { "gc.post", "network:api.example", "secret:admin-token" },
        };

        Assert.Equal(ModuleSignatureStatus.BadSignature, verifier.Verify(widened));
    }

    [Fact]
    public void Tampered_module_id_or_version_is_a_bad_signature()
    {
        var (verifier, key) = Setup();
        var signed = Sign(Unsigned(), key);

        Assert.Equal(ModuleSignatureStatus.BadSignature, verifier.Verify(signed with { ModuleId = "bazaar.other" }));
        Assert.Equal(ModuleSignatureStatus.BadSignature, verifier.Verify(signed with { Version = "1.2.1" }));
    }

    [Fact]
    public void Signature_from_a_different_key_is_a_bad_signature()
    {
        var (verifier, _) = Setup();
        using var attacker = NewKey();
        var forged = Sign(Unsigned(), attacker);

        Assert.Equal(ModuleSignatureStatus.BadSignature, verifier.Verify(forged));
    }

    [Fact]
    public void Unknown_key_id_is_a_typed_result_not_an_exception()
    {
        var (verifier, key) = Setup();
        var package = Sign(Unsigned(keyId: "publisher-key-unknown"), key);

        Assert.Equal(ModuleSignatureStatus.UnknownKey, verifier.Verify(package));
    }

    [Fact]
    public void Missing_signature_is_not_installable()
    {
        var (verifier, _) = Setup();

        Assert.Equal(ModuleSignatureStatus.MissingSignature, verifier.Verify(Unsigned()));
    }

    [Fact]
    public void Malformed_signature_bytes_are_a_bad_signature()
    {
        var (verifier, _) = Setup();
        var package = Unsigned() with { Signature = [1, 2, 3] };

        Assert.Equal(ModuleSignatureStatus.BadSignature, verifier.Verify(package));
    }

    [Fact]
    public void Blank_required_field_or_permission_is_malformed()
    {
        var (verifier, key) = Setup();

        Assert.Equal(ModuleSignatureStatus.Malformed, verifier.Verify(Sign(Unsigned(moduleId: " "), key)));
        Assert.Equal(ModuleSignatureStatus.Malformed, verifier.Verify(Sign(Unsigned(permissions: new HashSet<string> { "" }), key)));
    }

    [Fact]
    public void Revoked_key_never_verifies()
    {
        var (verifier, key) = Setup(revoked: true);

        Assert.Equal(ModuleSignatureStatus.KeyNotValid, verifier.Verify(Sign(Unsigned(), key)));
    }

    [Fact]
    public void Key_outside_its_validity_dates_never_verifies()
    {
        var (early, earlyKey) = Setup(notBefore: Now.AddDays(1));
        var (late, lateKey) = Setup(notAfter: Now);

        Assert.Equal(ModuleSignatureStatus.KeyNotValid, early.Verify(Sign(Unsigned(), earlyKey)));
        Assert.Equal(ModuleSignatureStatus.KeyNotValid, late.Verify(Sign(Unsigned(), lateKey)));
    }

    [Fact]
    public void Canonical_form_has_version_prefix_and_length_prefixed_fields()
    {
        var package = Unsigned(permissions: new HashSet<string>(StringComparer.Ordinal) { "b", "a" }, moduleId: "m", version: "v", digest: "d");

        var bytes = ModuleCanonicalForm.Build(package);

        var expected = new List<byte> { 0x01 };
        foreach (var field in new[] { "m", "v", "d", "a", "b" })
        {
            var utf8 = Encoding.UTF8.GetBytes(field);
            expected.AddRange(new byte[] { 0, 0, 0, (byte)utf8.Length });
            expected.AddRange(utf8);
        }

        Assert.Equal(expected.ToArray(), bytes);
    }

    [Fact]
    public void Canonical_form_does_not_depend_on_permission_insertion_order()
    {
        var first = Unsigned(permissions: new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" });
        var second = Unsigned(permissions: new HashSet<string>(StringComparer.Ordinal) { "c", "b", "a" });

        Assert.Equal(ModuleCanonicalForm.Build(first), ModuleCanonicalForm.Build(second));
    }

    [Fact]
    public void Trust_store_rejects_a_key_that_is_not_p256()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var store = new InMemoryTrustedKeyStore();

        Assert.Throws<ArgumentException>(() => store.Add(new TrustedModuleKey(KeyId, "publisher-1", p384, Now.AddDays(-1), Now.AddDays(1))));
    }

    [Fact]
    public void Trust_store_rejects_a_duplicate_key_id()
    {
        using var first = NewKey();
        using var second = NewKey();
        var store = new InMemoryTrustedKeyStore();
        store.Add(new TrustedModuleKey(KeyId, "publisher-1", first, Now.AddDays(-1), Now.AddDays(1)));

        Assert.Throws<ArgumentException>(() => store.Add(new TrustedModuleKey(KeyId, "publisher-1", second, Now.AddDays(-1), Now.AddDays(1))));
        Assert.Null(store.Find("no-such-key"));
    }
}
