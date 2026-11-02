namespace Bazaar.Server.Components.Tls;

/// <summary>
/// The ACME certificate source (docs/design/32 section 2). Not implemented in this build: every load fails with
/// <see cref="TlsCertificateSourceUnavailableException"/>, and nothing falls back to another source.
/// </summary>
public sealed class AcmeCertificateProvider : ICertificateProvider
{
    public TlsSourceKind Source => TlsSourceKind.Acme;

    public Task<ServerCertificate> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromException<ServerCertificate>(new TlsCertificateSourceUnavailableException(TlsSourceKind.Acme));
}
