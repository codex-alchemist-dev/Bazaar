using Bazaar.Server.Components.PrivacyAudit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Components.Remote;

public static class RemoteTokensServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITokenService"/>, <see cref="IRefreshCredentialStore"/> (Postgres, over the shared
    /// <c>NpgsqlDataSource</c>) and <see cref="RemoteTokenOptions"/>. The caller must register <see cref="IAccessTokenIssuer"/>,
    /// <see cref="PrivacyAuditRecorder"/> (with <c>AddBazaarPrivacyAudit</c>) and an <c>NpgsqlDataSource</c>.
    /// </summary>
    // TODO(component): register from host composition when the component model (PR #125) merges.
    public static IServiceCollection AddBazaarRemoteTokens(this IServiceCollection services, Action<RemoteTokenOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<RemoteTokenOptions>();
        if (configure is not null)
            options.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRefreshCredentialStore, PostgresRefreshCredentialStore>();
        services.TryAddSingleton<ITokenService, TokenService>();
        return services;
    }

    /// <summary>
    /// Registers the pairing service (doc 32 section 3): <see cref="IPairingService"/>, the Postgres offer store and
    /// <see cref="PairingOptions"/>. It shares the refresh store and token service, so call <see cref="AddBazaarRemoteTokens"/> first.
    /// </summary>
    public static IServiceCollection AddBazaarPairing(this IServiceCollection services, Action<PairingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<PairingOptions>();
        if (configure is not null)
            options.Configure(configure);
        services.TryAddSingleton<IPairingOfferStore, PostgresPairingOfferStore>();
        services.TryAddSingleton<IPairingService, PairingService>();
        return services;
    }
}
