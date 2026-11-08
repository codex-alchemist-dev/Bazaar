using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Local development authenticator: accepts exactly one bearer token and returns the owner principal. Every other credential,
/// including a missing one, is refused. Only wired when <c>Bazaar:DevAuth</c> is enabled; see <c>AddBazaarDevAuth</c>.
/// </summary>
public sealed class DevTokenAuthenticator : IConnectionAuthenticator
{
    private const string _bearerPrefix = "Bearer ";
    private const string _ownerKind = "owner";
    private const string _principalNamespace = "bazaar:dev-principal:";

    private readonly byte[] _tokenDigest;
    private readonly PrincipalRef _owner;

    /// <param name="token">The configured secret. Compared through its SHA-256 digest so the comparison length never depends on the input.</param>
    /// <param name="principalId">Owner principal id, as text (see <see cref="DevAuthOptions.PrincipalId"/>).</param>
    public DevTokenAuthenticator(string token, string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        _tokenDigest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _owner = new PrincipalRef(ToPrincipalGuid(principalId), _ownerKind);
    }

    /// <summary>The GUID for a principal id: a GUID is used as is, and other text maps to a stable GUID derived from its SHA-256 hash.</summary>
    public static Guid ToPrincipalGuid(string principalId)
    {
        if (Guid.TryParse(principalId, out var guid))
        {
            return guid;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(_principalNamespace + principalId));
        return new Guid(hash.AsSpan(0, 16));
    }

    public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken)
    {
        if (credential is null || string.IsNullOrWhiteSpace(credential.Value))
        {
            return ValueTask.FromResult(AuthResult.Fail("no credential was presented"));
        }

        var presented = credential.Value;
        if (presented.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[_bearerPrefix.Length..].Trim();
        }

        var presentedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        if (!CryptographicOperations.FixedTimeEquals(presentedDigest, _tokenDigest))
        {
            return ValueTask.FromResult(AuthResult.Fail("dev token does not match"));
        }

        return ValueTask.FromResult(AuthResult.Success(_owner));
    }
}
