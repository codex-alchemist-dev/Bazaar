namespace Bazaar.Server.Components.Tls;

/// <summary>
/// The overlay-issued certificate source (docs/design/32 section 2, unverified). Not implemented in this build: every load fails with
/// <see cref="TlsCertificateSourceUnavailableException"/>, and nothing falls back to another source.
/// </summary>
public sealed class OverlayCertificateProvider : ICertificateProvider
{
    public TlsSourceKind Source => TlsSourceKind.Overlay;

    public Task<ServerCertificate> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromException<ServerCertificate>(new TlsCertificateSourceUnavailableException(TlsSourceKind.Overlay));
}
