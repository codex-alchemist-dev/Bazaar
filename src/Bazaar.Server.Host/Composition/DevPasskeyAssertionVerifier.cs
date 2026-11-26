using System.Security.Cryptography;
using System.Text;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.SignIn;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Development stand-in for the passkey check on the sign-in page (doc 32 section 4), registered only while <c>Bazaar:DevAuth:Enabled</c>
/// is on. The "assertion" is the dev token (<see cref="DevAuthOptions.TokenSecret"/>), which already signs a client in as the dev
/// principal over the wire, so this adds no power the dev token does not have. It lets the whole sign-in path (challenge, assert, handoff
/// code, <c>/auth/handoff</c>, <c>/auth/whoami</c>) run against a real host before the WebAuthn adapter exists. Every assertion that is not
/// the token fails the same way a bad passkey does.
/// </summary>
public sealed class DevPasskeyAssertionVerifier(IOptions<DevAuthOptions> options, Func<string> token) : IPasskeyAssertionVerifier
{
    private readonly Guid _principal = DevTokenAuthenticator.ToPrincipalGuid(options.Value.PrincipalId);
    private readonly byte[] _digest = SHA256.HashData(Encoding.UTF8.GetBytes(token()));

    /// <inheritdoc />
    public Task<SignInIdentity?> VerifyAsync(string challenge, string assertionJson, CancellationToken ct = default)
    {
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(assertionJson ?? string.Empty));
        var ok = challenge.Length > 0 && CryptographicOperations.FixedTimeEquals(presented, _digest);
        return Task.FromResult(ok ? new SignInIdentity(_principal, PrincipalKind.Human, Guid.CreateVersion7()) : null);
    }
}
