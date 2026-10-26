using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Tests.Wire.Harness;

/// <summary>Protocol-harness authenticator: credentials are looked up in a table, no identity service involved.</summary>
public sealed class FakeAuthenticator : IConnectionAuthenticator
{
    private readonly Dictionary<string, AuthResult> _known = new(StringComparer.Ordinal);

    /// <summary>Every device the authenticator saw, in order.</summary>
    public List<DeviceInfo> Seen { get; } = [];

    /// <summary>Accepts <paramref name="credential"/> as <paramref name="principal"/>.</summary>
    public FakeAuthenticator Allow(string credential, PrincipalRef? principal = null, DateTimeOffset? expiresAt = null, string? deviceId = null)
    {
        _known[credential] = AuthResult.Success(principal ?? new PrincipalRef(Guid.NewGuid(), "human"), deviceId, expiresAt);
        return this;
    }

    /// <inheritdoc />
    public ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken)
    {
        lock (Seen) Seen.Add(device);
        return ValueTask.FromResult(
            credential is not null && _known.TryGetValue(credential.Value, out var result) ? result : AuthResult.Fail("unknown credential"));
    }
}
