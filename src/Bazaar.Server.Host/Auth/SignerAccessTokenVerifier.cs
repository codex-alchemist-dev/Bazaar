using Bazaar.Server.Http.Auth;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// Bridges the synchronous <see cref="IAccessTokenVerifier"/> to the identity signer. Keys are cached in memory, so a verify normally
/// does not touch the database. Any failure refuses the token (fail closed).
/// </summary>
public sealed class SignerAccessTokenVerifier(IAccessTokenSigner signer) : IAccessTokenVerifier
{
    private readonly IAccessTokenSigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));

    /// <inheritdoc />
    public bool TryVerify(string bearerToken, out AuthenticatedCaller caller)
    {
        caller = null!;
        ArgumentNullException.ThrowIfNull(bearerToken);

        AccessTokenClaims? claims;
        try
        {
            claims = _signer.VerifyAsync(bearerToken).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        if (claims is null) return false;
        caller = new AuthenticatedCaller(claims.DeviceId, claims.HumanId);
        return true;
    }
}
