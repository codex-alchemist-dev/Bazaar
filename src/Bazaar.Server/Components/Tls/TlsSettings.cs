namespace Bazaar.Server.Components.Tls;

/// <summary>
/// The <c>tls.certificate_source</c> setting (docs/design/32 settings table: enum pem, acme, overlay; default pem; install
/// scope; owner). It is a constant, not a registered <c>SettingKey</c>: <c>HostSettings</c> holds only <c>host.*</c> keys and
/// its test pins that list, and this component is not composed into the host yet.
/// </summary>
public static class TlsSettings
{
    public const string CertificateSourceName = "tls.certificate_source";

    public static TlsSourceKind DefaultCertificateSource => TlsSourceKind.Pem;

    /// <summary>
    /// Parses the stored value. Null, empty or white space means the default (<see cref="TlsSourceKind.Pem"/>). Any other
    /// text must name a source, ignoring case and surrounding white space.
    /// </summary>
    public static TlsSourceKind ParseCertificateSource(string? value)
    {
        if (!TryParseCertificateSource(value, out var kind))
            throw new FormatException($"'{CertificateSourceName}' must be one of pem, acme or overlay; got '{value}'.");
        return kind;
    }

    public static bool TryParseCertificateSource(string? value, out TlsSourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            kind = DefaultCertificateSource;
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "pem":
                kind = TlsSourceKind.Pem;
                return true;
            case "acme":
                kind = TlsSourceKind.Acme;
                return true;
            case "overlay":
                kind = TlsSourceKind.Overlay;
                return true;
            default:
                kind = default;
                return false;
        }
    }
}
