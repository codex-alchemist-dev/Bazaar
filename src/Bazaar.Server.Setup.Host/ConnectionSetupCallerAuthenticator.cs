using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// Adapts the host's <see cref="IConnectionAuthenticator"/> (the one the HTTP surface and the socket use) to the provisioning API's
/// <see cref="ISetupCallerAuthenticator"/>. A token is valid only when the authenticator accepts it and it has not expired, the same
/// rule the HTTP surface applies. The reason text of a failure is dropped.
/// </summary>
public sealed class ConnectionSetupCallerAuthenticator(IConnectionAuthenticator inner, TimeProvider time) : ISetupCallerAuthenticator
{
    private static readonly DeviceInfo _device = new("setup", "http", string.Empty);

    /// <inheritdoc />
    public async ValueTask<SetupPrincipal?> AuthenticateAsync(string bearerToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bearerToken);
        var result = await inner.AuthenticateAsync(new HelloCredential(bearerToken), _device, cancellationToken).ConfigureAwait(false);
        if (!result.Ok || result.Principal is null)
        {
            return null;
        }

        if (result.ExpiresAt is { } expiry && expiry <= time.GetUtcNow())
        {
            return null;
        }

        return new SetupPrincipal(result.Principal.Id, result.Principal.Kind);
    }
}
