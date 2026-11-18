using Bazaar.Server.Components.Remote;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Host.Auth;

/// <summary>Bridges the synchronous <see cref="IAccessTokenIssuer"/> to the identity signer. Signing throws when the key cannot be unwrapped.</summary>
public sealed class SignerAccessTokenIssuer(IAccessTokenSigner signer) : IAccessTokenIssuer
{
    private readonly IAccessTokenSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));

    /// <summary>
    /// Signs the token. Any failure (a key that cannot be unwrapped, a store that cannot be reached, a signer that is unavailable) fails
    /// closed as <see cref="AccessTokenIssuerUnavailableException"/>, so routes answer 503 rather than 500. The cause is kept as the inner exception.
    /// </summary>
    /// <inheritdoc />
    public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt)
    {
        try
        {
            return _signer.SignAsync(deviceId, humanId, expiresAt).AsTask().GetAwaiter().GetResult();
        }
        catch (AccessTokenIssuerUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AccessTokenIssuerUnavailableException(ex);
        }
    }
}
