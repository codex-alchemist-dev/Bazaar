using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Bazaar.Server.Components.Tls;

/// <summary>Loads an operator-supplied PEM certificate file and its PEM private key file.</summary>
public sealed class PemCertificateProvider : ICertificateProvider
{
    private readonly string _certificatePath;
    private readonly string _keyPath;

    public PemCertificateProvider(string certificatePath, string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(certificatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);
        _certificatePath = certificatePath;
        _keyPath = keyPath;
    }

    public TlsSourceKind Source => TlsSourceKind.Pem;

    public Task<ServerCertificate> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPemFile(_certificatePath, _keyPath);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            throw new TlsCertificateException(_certificatePath, "the PEM certificate or key file could not be read, or does not match.", ex);
        }

        try
        {
            return Task.FromResult(ServerCertificate.Create(certificate, _certificatePath));
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }
}
