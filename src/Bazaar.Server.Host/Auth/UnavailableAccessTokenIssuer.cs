using Bazaar.Server.Components.Remote;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// The issuer registered when no real or dev issuer is enabled. Pairing and refresh still resolve, but any attempt to issue a token
/// throws <see cref="AccessTokenIssuerUnavailableException"/>, which the routes answer with 503 <c>unavailable</c>.
/// </summary>
public sealed class UnavailableAccessTokenIssuer : IAccessTokenIssuer
{
    public string Issue(Guid deviceId, Guid humanId, DateTimeOffset expiresAt) => throw new AccessTokenIssuerUnavailableException();
}
