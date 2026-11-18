using Bazaar.Server.Components.Remote;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Host.Auth;

/// <summary>Bridges the synchronous <see cref="IAccessTokenIssuer"/> to the identity signer. Signing throws when the key cannot be unwrapped.</summary>
public sealed class SignerAccessTokenIssuer(IAccessTokenSigner signer) : IAccessTokenIssuer
{
    private readonly IAccessTokenSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));

    /// <inheritdoc />
    public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt) =>
        _signer.SignAsync(deviceId, humanId, expiresAt).AsTask().GetAwaiter().GetResult();
}
