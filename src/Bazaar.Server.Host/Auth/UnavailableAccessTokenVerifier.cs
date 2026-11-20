using Bazaar.Server.Http.Auth;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// The verifier registered when no access-token issuer is enabled. Every token is refused, so no request gets a caller.
/// </summary>
public sealed class UnavailableAccessTokenVerifier : IAccessTokenVerifier
{
    public bool TryVerify(string bearerToken, out AuthenticatedCaller caller)
    {
        caller = null!;
        return false;
    }
}
