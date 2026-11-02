namespace Bazaar.Server.Components.Tls;

/// <summary>Supplies the TLS server certificate for the listener (docs/design/32 section 2). Each implementation owns one source.</summary>
public interface ICertificateProvider
{
    /// <summary>The source this provider implements.</summary>
    TlsSourceKind Source { get; }

    /// <summary>
    /// Loads the certificate and its public-key hash. The caller disposes the returned certificate. Failures throw
    /// <see cref="TlsCertificateException"/>, or <see cref="TlsCertificateSourceUnavailableException"/> for a source this build lacks.
    /// </summary>
    Task<ServerCertificate> LoadAsync(CancellationToken cancellationToken);
}
