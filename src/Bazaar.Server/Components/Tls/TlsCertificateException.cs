namespace Bazaar.Server.Components.Tls;

/// <summary>A certificate could not be loaded. The message names the file or source, never key material or a secret value.</summary>
public sealed class TlsCertificateException : Exception
{
    public TlsCertificateException(string origin, string reason, Exception? innerException = null)
        : base($"TLS certificate '{origin}' could not be loaded: {reason}", innerException)
    {
        Origin = origin;
    }

    /// <summary>The file path that failed.</summary>
    public string Origin { get; }
}

/// <summary>The configured certificate source is not implemented in this build. Thrown instead of falling back to another source.</summary>
public sealed class TlsCertificateSourceUnavailableException : Exception
{
    public TlsCertificateSourceUnavailableException(TlsSourceKind source)
        : base($"The TLS certificate source '{ToSettingValue(source)}' is not available in this build. Set tls.certificate_source to pem.")
    {
        SourceKind = source;
    }

    public TlsSourceKind SourceKind { get; }

    private static string ToSettingValue(TlsSourceKind source) => source switch
    {
        TlsSourceKind.Pem => "pem",
        TlsSourceKind.Acme => "acme",
        TlsSourceKind.Overlay => "overlay",
        _ => source.ToString(),
    };
}
