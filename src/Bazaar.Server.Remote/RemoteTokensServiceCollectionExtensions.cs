using Bazaar.Server.PrivacyAudit;
using Bazaar.PrivacyAudit;
using Bazaar.Server.WebAdmin;
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
        // Read ports for the admin panel (doc 51 section 5). A missing NpgsqlDataSource fails at resolve time, as for the stores.
        services.TryAddSingleton<PostgresAdminDirectory>();
        services.TryAddSingleton<IAdminUserDirectory>(sp => sp.GetRequiredService<PostgresAdminDirectory>());
        services.TryAddSingleton<IAdminDeviceDirectory>(sp => sp.GetRequiredService<PostgresAdminDirectory>());
        services.TryAddSingleton<SignedInPersonLookup>();
        return services;
    }
}
