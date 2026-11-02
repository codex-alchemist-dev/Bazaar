using Bazaar.Server.Secrets;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Components.Tls;

/// <summary>What the operator configured for the TLS certificate. Paths and references only, never secret values.</summary>
/// <param name="Source">The configured <c>tls.certificate_source</c>.</param>
/// <param name="CertificatePath">PEM certificate file. Used with <paramref name="KeyPath"/> for the Pem source.</param>
/// <param name="KeyPath">PEM private key file.</param>
/// <param name="PfxPath">PFX file. Used instead of the PEM pair for the Pem source.</param>
/// <param name="PfxPasswordReference">Secret reference for the PFX password, or null when the file has none.</param>
public sealed record TlsOptions(
    TlsSourceKind Source,
    string? CertificatePath = null,
    string? KeyPath = null,
    string? PfxPath = null,
    string? PfxPasswordReference = null);

public static class TlsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICertificateProvider"/> for the configured source. The options are checked now, so a bad source
    /// fails at startup and not on the first load. A PFX file needs <see cref="ISecretResolver"/> in the container.
    /// Not called from host composition yet.
    /// </summary>
    public static IServiceCollection AddBazaarTls(this IServiceCollection services, TlsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ValidatePemOptions(options);

        services.AddSingleton<ICertificateProvider>(serviceProvider => options.Source switch
        {
            TlsSourceKind.Pem when options.PfxPath is not null => new PfxCertificateProvider(
                options.PfxPath,
                serviceProvider.GetRequiredService<ISecretResolver>(),
                options.PfxPasswordReference),
            TlsSourceKind.Pem => new PemCertificateProvider(options.CertificatePath!, options.KeyPath!),
            TlsSourceKind.Acme => new AcmeCertificateProvider(),
            TlsSourceKind.Overlay => new OverlayCertificateProvider(),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.Source, "Unknown TLS certificate source."),
        });
        return services;
    }

    private static void ValidatePemOptions(TlsOptions options)
    {
        if (options.Source != TlsSourceKind.Pem)
            return;

        if (options.PfxPath is not null)
        {
            if (options.CertificatePath is not null || options.KeyPath is not null)
                throw new ArgumentException("Give either a PFX file or a PEM certificate and key, not both.", nameof(options));
            return;
        }

        if (string.IsNullOrWhiteSpace(options.CertificatePath) || string.IsNullOrWhiteSpace(options.KeyPath))
            throw new ArgumentException("The pem source needs a PEM certificate path and a key path, or a PFX path.", nameof(options));
    }
}
