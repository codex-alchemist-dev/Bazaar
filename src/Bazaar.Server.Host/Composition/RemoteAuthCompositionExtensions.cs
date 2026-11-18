using Bazaar.PrivacyAudit;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Http.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Sign-in tokens and refresh (doc 32 section 5) for the host. Device pairing is removed (owner, 2026-11-16). The access-token issuer is the dev issuer only when
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
        // One dev issuer instance signs and verifies, so the verifier accepts exactly the tokens this process issued. It is registered
        // always, but it is handed out only when the dev gate is open.
        services.TryAddSingleton(sp => new DevAccessTokenIssuer(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IAccessTokenIssuer>(sp =>
            DevIssuerEnabled(sp, configuration)
                ? sp.GetRequiredService<DevAccessTokenIssuer>()
                : new UnavailableAccessTokenIssuer());
        services.TryAddSingleton<IAccessTokenVerifier>(sp =>
            DevIssuerEnabled(sp, configuration)
                ? new DevAccessTokenVerifier(sp.GetRequiredService<DevAccessTokenIssuer>())
                : new UnavailableAccessTokenVerifier());

        // Dev-only owner row for the dev issuer. Registered only with the flag; DevOwnerSeed also checks Development when it starts.
        if (configuration.GetValue<bool>(DevIssuerEnabledKey))
            services.AddHostedService<DevOwnerSeed>();
        return services;
    }

    /// <summary>The dev gate: the configuration flag AND the Development environment.</summary>
    private static bool DevIssuerEnabled(IServiceProvider services, IConfiguration configuration)
    {
        var environment = services.GetService<IHostEnvironment>();
        return configuration.GetValue<bool>(DevIssuerEnabledKey) && environment is not null && environment.IsDevelopment();
    }
}
