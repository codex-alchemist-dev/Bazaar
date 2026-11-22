using Bazaar.Client.Tui.Dashboard;
using Bazaar.PrivacyAudit;
using Bazaar.Server.WebAdmin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Components.Remote;

public static class RemoteAdminServiceCollectionExtensions
{
    /// <summary>
    /// Registers the identity read ports of the web admin (<see cref="IAdminUserDirectory"/>, <see cref="IAdminDeviceDirectory"/>)
    /// over <see cref="IIdentityDirectoryStore"/>, and the TUI's <see cref="IOpsDeviceRevoker"/> over <see cref="ITokenService"/>.
    /// Every registration is TryAdd. The store and the token service come from <c>AddBazaarRemoteTokens</c> (or the host); without
    /// them the ports cannot resolve and the sections show "unavailable".
    /// </summary>
    public static IServiceCollection AddBazaarRemoteAdminPorts(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IIdentityDirectoryStore>(sp => (IIdentityDirectoryStore)sp.GetRequiredService<IRefreshCredentialStore>());
        services.TryAddSingleton<IAdminUserDirectory>(sp => new RemoteAdminUserDirectory(sp.GetRequiredService<IIdentityDirectoryStore>()));
        services.TryAddSingleton<IAdminDeviceDirectory>(sp => new RemoteAdminDeviceDirectory(sp.GetRequiredService<IIdentityDirectoryStore>()));
        services.TryAddSingleton<IOpsDeviceRevoker>(sp => new RemoteOpsDeviceRevoker(sp.GetRequiredService<ITokenService>()));
        return services;
    }

    /// <summary>
    /// For a process that only revokes (the TUI): registers <see cref="IRefreshCredentialStore"/> over the data source and an
    /// <see cref="ITokenService"/> whose access-token issuer always refuses, so revocation works and nothing can be issued. Needs
    /// <c>NpgsqlDataSource</c>, <see cref="PrivacyAuditRecorder"/> and <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddBazaarRemoteRevokeOnly(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<RemoteTokenOptions>();
        services.TryAddSingleton<IRefreshCredentialStore, PostgresRefreshCredentialStore>();
        services.TryAddSingleton<IAccessTokenIssuer, RefusingAccessTokenIssuer>();
        services.TryAddSingleton<ITokenService, TokenService>();
        return services;
    }

    private sealed class RefusingAccessTokenIssuer : IAccessTokenIssuer
    {
        public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt) => throw new AccessTokenIssuerUnavailableException();
    }
}
