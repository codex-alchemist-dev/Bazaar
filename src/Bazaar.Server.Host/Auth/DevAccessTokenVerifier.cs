using Bazaar.Server.Http.Auth;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// DEV ONLY. Verifies tokens with the same <see cref="DevAccessTokenIssuer"/> instance that signed them, so only this process's tokens pass.
/// Registered with the dev issuer (<c>RemoteAuthCompositionExtensions</c>); see that class for the gate.
/// </summary>
public sealed class DevAccessTokenVerifier(DevAccessTokenIssuer issuer) : IAccessTokenVerifier
{
    private readonly DevAccessTokenIssuer _issuer = issuer ?? throw new ArgumentNullException(nameof(issuer));

    public bool TryVerify(string bearerToken, out AuthenticatedCaller caller)
    {
        caller = null!;
        ArgumentNullException.ThrowIfNull(bearerToken);
        if (!_issuer.TryVerify(bearerToken, out var claims)) return false;
        caller = new AuthenticatedCaller(claims.DeviceId, claims.HumanId);
        return true;
    }
}
