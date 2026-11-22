using Bazaar.Server.Components.Remote;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// The signer registered when no signing key store or key-encryption key is available. Signing fails with
/// <see cref="AccessTokenIssuerUnavailableException"/> (routes answer 503); every token is refused.
/// </summary>
public sealed class UnavailableAccessTokenSigner : IAccessTokenSigner
{
    /// <inheritdoc />
    public ValueTask<string> SignAsync(Guid deviceId, Guid humanId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        ValueTask.FromException<string>(new AccessTokenIssuerUnavailableException());

    /// <inheritdoc />
    public ValueTask<AccessTokenClaims?> VerifyAsync(string token, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AccessTokenClaims?>(null);
}
