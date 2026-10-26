using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Host.Composition;

/// <summary>Placeholder authenticator that refuses every credential. Replaced when the Identity Service lands, so no unauthenticated connection can ever be accepted.</summary>
public sealed class DenyAllAuthenticator : IConnectionAuthenticator
{
    public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken) =>
        ValueTask.FromResult(AuthResult.Fail("no authenticator is configured"));
}
