using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Updates;

namespace Bazaar.Updates.Tests;

public sealed class PinnedCertificateSignatureVerifierTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var item in _owned)
        {
            item.Dispose();
        }
    }

    private X509Certificate2 NewCertificate(string subject, out RSA privateKey)
    {
        var rsa = RSA.Create(2048);
        _owned.Add(rsa);
        privateKey = rsa;
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        _owned.Add(cert);
        return cert;
    }

    private static byte[] Sign(RSA key, ReadOnlySpan<byte> package) =>
        key.SignData(package, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    [Fact]
    public void A_signature_from_the_pinned_key_verifies()
    {
        var cert = NewCertificate("CN=Company signing", out var key);
        var verifier = new PinnedCertificateSignatureVerifier(cert);
        var package = "release package bytes"u8.ToArray();

        Assert.True(verifier.Verify(package, Sign(key, package)));
    }

    [Fact]
    public void A_signature_over_different_bytes_is_refused()
    {
        var cert = NewCertificate("CN=Company signing", out var key);
        var verifier = new PinnedCertificateSignatureVerifier(cert);
        var signature = Sign(key, "release package bytes"u8);

        Assert.False(verifier.Verify("tampered package bytes"u8, signature));
    }

    [Fact]
    public void A_signature_from_another_key_is_refused()
    {
        var pinned = NewCertificate("CN=Company signing", out _);
        var verifier = new PinnedCertificateSignatureVerifier(pinned);
        var package = "release package bytes"u8.ToArray();

        var attackerCert = NewCertificate("CN=Attacker", out var attackerKey);
        Assert.NotNull(attackerCert);

        Assert.False(verifier.Verify(package, Sign(attackerKey, package)));
    }

    [Fact]
    public void An_empty_signature_is_refused()
    {
        var cert = NewCertificate("CN=Company signing", out _);
        var verifier = new PinnedCertificateSignatureVerifier(cert);

        Assert.False(verifier.Verify("release package bytes"u8, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Thumbprint_is_the_pinned_certificate_thumbprint()
    {
        var cert = NewCertificate("CN=Company signing", out _);
        var verifier = new PinnedCertificateSignatureVerifier(cert);

        Assert.Equal(cert.Thumbprint, verifier.Thumbprint);
    }

    [Fact]
    public void Verifying_through_the_manifest_path_needs_the_hash_and_the_signature_to_match()
    {
        var cert = NewCertificate("CN=Company signing", out var key);
        var verifier = new PinnedCertificateSignatureVerifier(cert);
        var package = "release package bytes"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
        var manifest = new UpdateManifest("0.2.0", new Uri("https://company.example/Bazaar.msix"), hash, Sign(key, package));

        Assert.True(UpdateVerifier.Verify(manifest, package, verifier));
        Assert.False(UpdateVerifier.Verify(manifest, "other bytes"u8, verifier));
    }
}
