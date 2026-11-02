using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Components.Tls;

/// <summary>
/// Loads an operator-supplied PKCS#12 (PFX) file. The password is never in configuration: it is a secret reference
/// (<c>env:NAME</c> or <c>file:PATH</c>) resolved through <see cref="ISecretResolver"/> when the certificate loads.
/// </summary>
public sealed class PfxCertificateProvider : ICertificateProvider
{
    private readonly string _pfxPath;
    private readonly ISecretResolver _secretResolver;
    private readonly string? _passwordReference;

    /// <param name="pfxPath">Path to the PFX file.</param>
    /// <param name="secretResolver">Resolves <paramref name="passwordReference"/>.</param>
    /// <param name="passwordReference">Secret reference for the PFX password, or null when the file has none.</param>
    public PfxCertificateProvider(string pfxPath, ISecretResolver secretResolver, string? passwordReference = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pfxPath);
        ArgumentNullException.ThrowIfNull(secretResolver);
        _pfxPath = pfxPath;
        _secretResolver = secretResolver;
        _passwordReference = passwordReference;
    }

    public TlsSourceKind Source => TlsSourceKind.Pem;

    public async Task<ServerCertificate> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // No reference means a PFX with no password. A failed resolution throws SecretResolutionException, which names
        // the reference and never the value.
        var password = _passwordReference is null
            ? string.Empty
            : await _secretResolver.ResolveAsync(_passwordReference, cancellationToken);

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(_pfxPath, password);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException)
        {
            throw new TlsCertificateException(_pfxPath, "the PFX file could not be read, or the password is wrong.", ex);
        }

        try
        {
            return ServerCertificate.Create(certificate, _pfxPath);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }
}
