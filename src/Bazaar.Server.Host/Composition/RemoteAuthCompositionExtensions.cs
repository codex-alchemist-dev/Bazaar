using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.PrivacyAudit;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.Identity.Postgres;
using Bazaar.Server.Identity.Signing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Sign-in tokens and refresh (doc 32 section 5) for the host. Access tokens are signed by the production identity signer
/// (<see cref="AccessTokenSigner"/>) under the access-token signing key. The key is stored wrapped by the host KEK in <c>soul_dek</c> when the
/// host has a database. In Development without a database the key is kept in memory. With neither, the signer is
/// <see cref="UnavailableAccessTokenSigner"/>, so routes answer 503 instead of minting tokens. Register after <c>AddBazaarComponents</c>, which
/// provides the audit log, the shared <c>NpgsqlDataSource</c> and the host KEK these services need.
/// </summary>
public static class RemoteAuthCompositionExtensions
{
    /// <summary>
    /// Configuration key for the dev owner seed (<see cref="DevOwnerSeed"/>). It no longer selects the token issuer. The seed also needs the
    /// Development environment.
    /// </summary>
    public const string DevIssuerEnabledKey = "Bazaar:Remote:DevIssuer:Enabled";

    /// <summary>Configuration section for the access-token signing settings (<see cref="AccessTokenSigningOptions"/>).</summary>
    public const string SigningSection = "Bazaar:Remote:Signing";

    public static IServiceCollection AddBazaarRemoteAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddBazaarPrivacyAudit();
        services.AddBazaarRemoteTokens();
        // Identity read ports and the device revoker for the web admin and the server TUI (docs 51 and 58).
        services.AddBazaarRemoteAdminPorts();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(ReadSigningOptions(configuration));
        if (services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
            services.AddBazaarPostgresSigningKeys();
        services.TryAddSingleton<IAccessTokenSigner>(CreateSigner);
        services.TryAddSingleton<IAccessTokenIssuer>(sp => new SignerAccessTokenIssuer(sp.GetRequiredService<IAccessTokenSigner>()));
        services.TryAddSingleton<IAccessTokenVerifier>(sp => new SignerAccessTokenVerifier(sp.GetRequiredService<IAccessTokenSigner>()));

        // Dev-only owner row. Registered only with the flag; DevOwnerSeed also checks Development when it starts.
        if (configuration.GetValue<bool>(DevIssuerEnabledKey))
            services.AddHostedService<DevOwnerSeed>();
        return services;
    }

    /// <summary>Reads the signing settings from <see cref="SigningSection"/>; absent keys keep the documented defaults.</summary>
    private static AccessTokenSigningOptions ReadSigningOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection(SigningSection);
        var defaults = new AccessTokenSigningOptions();
        return new AccessTokenSigningOptions
        {
            SigningKeyRotationDays = section.GetValue("SigningKeyRotationDays", defaults.SigningKeyRotationDays),
            AccessTokenLifetime = TimeSpan.FromMinutes(section.GetValue("AccessTokenLifetimeMinutes", defaults.AccessTokenLifetime.TotalMinutes)),
            ClockSkew = TimeSpan.FromSeconds(section.GetValue("ClockSkewSeconds", defaults.ClockSkew.TotalSeconds)),
        };
    }

    /// <summary>The live signer, or <see cref="UnavailableAccessTokenSigner"/> when no key store or KEK is available.</summary>
    private static IAccessTokenSigner CreateSigner(IServiceProvider sp)
    {
        var kek = sp.GetService<IKekProvider>();
        var store = sp.GetService<ISigningKeyStore>()
            ?? (IsDevelopment(sp) ? new InMemorySigningKeyStore() : null);
        if (kek is null || store is null) return new UnavailableAccessTokenSigner();

        return new AccessTokenSigner(
            store,
            new SigningKeyKekWrapper(kek),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AccessTokenSigningOptions>());
    }

    private static bool IsDevelopment(IServiceProvider services) =>
        services.GetService<IHostEnvironment>() is { } environment && environment.IsDevelopment();
}
