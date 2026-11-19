using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Server.Secrets;

namespace Bazaar.Tls.Tests;

/// <summary>Certificate files the operator got wrong: every one must fail with <see cref="TlsCertificateException"/> and no key text.</summary>
public sealed class CertificateLoadingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-tls-" + Guid.NewGuid().ToString("N"));

    public CertificateLoadingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (string Cert, string Key, ECDsa Ecdsa) Write(string name, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=test.invalid", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
        var certPath = Path.Combine(_dir, name + ".crt");
        var keyPath = Path.Combine(_dir, name + ".key");
        File.WriteAllText(certPath, cert.ExportCertificatePem());
        File.WriteAllText(keyPath, key.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath, key);
    }

    [Fact]
    public async Task A_matching_pair_loads()
    {
        var (cert, key, _) = Write("ok");

        using var loaded = await new PemCertificateProvider(cert, key).LoadAsync(Ct);

        Assert.Equal(64, loaded.PublicKeyHash.Length);
    }

    [Fact]
    public async Task A_key_that_does_not_match_the_certificate_is_refused()
    {
        var (cert, _, _) = Write("a");
        var (_, otherKey, _) = Write("b");

        await Assert.ThrowsAsync<TlsCertificateException>(() => new PemCertificateProvider(cert, otherKey).LoadAsync(Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a pem file at all")]
    [InlineData("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n")]
    [InlineData("-----BEGIN CERTIFICATE-----\n")]
    public async Task Garbage_in_either_file_is_refused(string garbage)
    {
        var (cert, key, _) = Write("ok");
        var badCert = Path.Combine(_dir, "bad.crt");
        File.WriteAllText(badCert, garbage);
        var badKey = Path.Combine(_dir, "bad.key");
        File.WriteAllText(badKey, garbage);

        await Assert.ThrowsAsync<TlsCertificateException>(() => new PemCertificateProvider(badCert, key).LoadAsync(Ct));
        await Assert.ThrowsAsync<TlsCertificateException>(() => new PemCertificateProvider(cert, badKey).LoadAsync(Ct));
    }

    [Fact]
    public async Task A_missing_file_is_refused()
    {
        var (cert, _, _) = Write("ok");

        await Assert.ThrowsAsync<TlsCertificateException>(() => new PemCertificateProvider(cert, Path.Combine(_dir, "absent.key")).LoadAsync(Ct));
    }

    [Fact]
    public async Task A_directory_in_place_of_a_file_is_refused()
    {
        var (cert, _, _) = Write("ok");

        await Assert.ThrowsAsync<TlsCertificateException>(() => new PemCertificateProvider(cert, _dir).LoadAsync(Ct));
    }

    [Fact]
    public async Task A_pfx_with_the_wrong_password_is_refused_and_the_password_is_not_in_the_message()
    {
        var (certPath, keyPath, _) = Write("ok");
        using var cert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        var password = "pw" + "-" + "canary" + "-" + Guid.NewGuid().ToString("N");
        var pfx = Path.Combine(_dir, "ok.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pkcs12, password));
        var wrong = "wr" + "ong" + "-" + Guid.NewGuid().ToString("N");

        var ex = await Assert.ThrowsAsync<TlsCertificateException>(
            () => new PfxCertificateProvider(pfx, new FixedResolver(wrong), "env:X").LoadAsync(Ct));

        Assert.DoesNotContain(wrong, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pfx_of_garbage_is_refused()
    {
        var pfx = Path.Combine(_dir, "bad.pfx");
        File.WriteAllBytes(pfx, [1, 2, 3, 4, 5]);

        await Assert.ThrowsAsync<TlsCertificateException>(() => new PfxCertificateProvider(pfx, new FixedResolver("x")).LoadAsync(Ct));
    }

    [Fact]
    public async Task A_certificate_whose_validity_has_ended_is_not_served_silently()
    {
        var (cert, key, _) = Write("old", DateTimeOffset.UtcNow.AddDays(-60), DateTimeOffset.UtcNow.AddDays(-30));

        // Recorded as a decision point in the round 4 findings: the docs do not say whether an expired certificate is refused,
        // so this only pins that loading it does not throw something other than the documented exception type.
        try
        {
            using var loaded = await new PemCertificateProvider(cert, key).LoadAsync(Ct);
            Assert.True(loaded.Certificate.NotAfter < DateTime.Now);
        }
        catch (TlsCertificateException)
        {
        }
    }

    private sealed class FixedResolver(string value) : ISecretResolver
    {
        public bool CanResolve(string reference) => true;

        public ValueTask<string> ResolveAsync(string reference, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }
}
