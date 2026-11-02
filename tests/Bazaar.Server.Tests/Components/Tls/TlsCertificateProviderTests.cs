using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Server.Components.Tls;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Tests.Components.Tls;

/// <summary>Loads a generated self-signed certificate from PEM and PFX files and checks the public-key pin.</summary>
public sealed class TlsCertificateProviderTests : IDisposable
{
    private const string _password = "test-pfx-password";
    private const string _passwordVariable = "BAZAAR_TEST_PFX_PASSWORD";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bazaar-tls-" + Guid.NewGuid().ToString("N"));

    public TlsCertificateProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    /// <summary>A self-signed ECDSA P-256 certificate and its key, made in memory.</summary>
    private static (X509Certificate2 Certificate, ECDsa Key) CreateSelfSigned()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=bazaar-test.local", key, HashAlgorithmName.SHA256);
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (certificate, key);
    }

    /// <summary>The pin recomputed by hand, independent of the production helper.</summary>
    private static string Expected(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    [Fact]
    public async Task Pem_provider_loads_the_certificate_with_a_stable_public_key_pin()
    {
        var (source, key) = CreateSelfSigned();
        using var _ = source;
        using (key)
        {
            var certPath = Path.Combine(_directory, "server.crt");
            var keyPath = Path.Combine(_directory, "server.key");
            await File.WriteAllTextAsync(certPath, source.ExportCertificatePem(), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);

            var provider = new PemCertificateProvider(certPath, keyPath);
            using var first = await provider.LoadAsync(TestContext.Current.CancellationToken);
            using var second = await provider.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(TlsSourceKind.Pem, provider.Source);
            Assert.True(first.Certificate.HasPrivateKey);
            Assert.Equal(source.Thumbprint, first.Certificate.Thumbprint);
            Assert.Equal(Expected(source), first.PublicKeyHash);
            Assert.Equal(first.PublicKeyHash, second.PublicKeyHash);
            Assert.Matches("^[0-9a-f]{64}$", first.PublicKeyHash);
        }
    }

    [Fact]
    public async Task Pfx_provider_resolves_the_password_reference_and_gives_the_same_pin()
    {
        var (source, key) = CreateSelfSigned();
        using var _ = source;
        using (key)
        {
            var pfxPath = Path.Combine(_directory, "server.pfx");
            await File.WriteAllBytesAsync(pfxPath, source.Export(X509ContentType.Pfx, _password), TestContext.Current.CancellationToken);

            var resolver = new BootstrapSecretResolver(name => name == _passwordVariable ? _password : null);
            var provider = new PfxCertificateProvider(pfxPath, resolver, "env:" + _passwordVariable);
            using var loaded = await provider.LoadAsync(TestContext.Current.CancellationToken);

            Assert.True(loaded.Certificate.HasPrivateKey);
            Assert.Equal(Expected(source), loaded.PublicKeyHash);
        }
    }

    [Fact]
    public async Task Pfx_provider_reports_a_missing_secret_without_loading_the_file()
    {
        var resolver = new BootstrapSecretResolver(_ => null);
        var provider = new PfxCertificateProvider(Path.Combine(_directory, "absent.pfx"), resolver, "env:" + _passwordVariable);

        await Assert.ThrowsAsync<SecretResolutionException>(() => provider.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pem_provider_reports_a_missing_file_with_the_path_and_no_key_material()
    {
        var certPath = Path.Combine(_directory, "missing.crt");
        var provider = new PemCertificateProvider(certPath, Path.Combine(_directory, "missing.key"));

        var error = await Assert.ThrowsAsync<TlsCertificateException>(() => provider.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(certPath, error.Origin);
    }

    [Fact]
    public async Task Acme_and_overlay_providers_throw_the_typed_not_available_exception()
    {
        var acme = new AcmeCertificateProvider();
        var overlay = new OverlayCertificateProvider();

        var acmeError = await Assert.ThrowsAsync<TlsCertificateSourceUnavailableException>(() => acme.LoadAsync(TestContext.Current.CancellationToken));
        var overlayError = await Assert.ThrowsAsync<TlsCertificateSourceUnavailableException>(() => overlay.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(TlsSourceKind.Acme, acmeError.SourceKind);
        Assert.Equal(TlsSourceKind.Overlay, overlayError.SourceKind);
        Assert.Contains("not available in this build", acmeError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Add_bazaar_tls_refuses_a_pem_source_without_both_paths()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddBazaarTls(new TlsOptions(TlsSourceKind.Pem, CertificatePath: "server.crt")));
        Assert.Throws<ArgumentException>(() => services.AddBazaarTls(new TlsOptions(TlsSourceKind.Pem, "server.crt", "server.key", PfxPath: "server.pfx")));
    }
}
