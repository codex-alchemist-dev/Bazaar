namespace Bazaar.Server.Http.Auth;

/// <summary>
/// Checks a bearer access token's signature and expiry and reads its device and human. Expiry is judged by the verifier's own clock.
/// Does not check revocation; the caller of the verifier must look the device up for that.
/// </summary>
public interface IAccessTokenVerifier
{
    /// <summary>True when the token is valid; <paramref name="caller"/> is set only then and is null otherwise.</summary>
    bool TryVerify(string bearerToken, out AuthenticatedCaller caller);
}
