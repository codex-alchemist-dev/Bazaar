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
}
