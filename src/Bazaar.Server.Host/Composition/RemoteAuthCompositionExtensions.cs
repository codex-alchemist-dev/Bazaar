using Bazaar.Server.Components.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Pairing (doc 32 section 3) and refresh (section 5) for the host. The access-token issuer is the dev issuer only when
/// <c>Bazaar:Remote:DevIssuer:Enabled</c> is true AND the host environment is Development. Otherwise it is
/// <see cref="UnavailableAccessTokenIssuer"/>, so the routes answer 503 instead of minting tokens. Register after <c>AddBazaarComponents</c>,
/// which provides the audit log and the shared <c>NpgsqlDataSource</c> these services need.
/// </summary>
public static class RemoteAuthCompositionExtensions
{
    /// <summary>Configuration key that opts in to the dev issuer. Development environment is also required.</summary>
    public const string DevIssuerEnabledKey = "Bazaar:Remote:DevIssuer:Enabled";

    public static IServiceCollection AddBazaarRemoteAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddBazaarPrivacyAudit();
        services.AddBazaarRemoteTokens();
        services.AddBazaarPairing();
        services.TryAddSingleton<IAccessTokenIssuer>(sp =>
        {
            var environment = sp.GetService<IHostEnvironment>();
            var devEnabled = configuration.GetValue<bool>(DevIssuerEnabledKey) && environment is not null && environment.IsDevelopment();
            return devEnabled
                ? new DevAccessTokenIssuer(sp.GetRequiredService<TimeProvider>())
                : new UnavailableAccessTokenIssuer();
        });

        // Dev-only owner row for the dev issuer. Registered only with the flag; DevOwnerSeed also checks Development when it starts.
        if (configuration.GetValue<bool>(DevIssuerEnabledKey))
            services.AddHostedService<DevOwnerSeed>();
        return services;
    }
}
