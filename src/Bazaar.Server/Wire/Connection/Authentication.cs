using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Wire.Connection;

/// <summary>The GC model's view of a wire principal.</summary>
public static class PrincipalRefGcExtensions
{
    /// <summary>The GC model's kind for this principal; the owner reads all, so the kind must reach the read rules.</summary>
    /// <exception cref="InvalidOperationException">The wire kind is not one the GC model knows.</exception>
    public static GcParticipantKind GcKind(this PrincipalRef principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Kind switch
        {
            "owner" => GcParticipantKind.Owner,
            "human" => GcParticipantKind.Human,
            "agent" => GcParticipantKind.Agent,
            _ => throw new InvalidOperationException($"Unknown principal kind '{principal.Kind}'."),
        };
    }
}

/// <summary>What the client presented. The credential comes from the upgrade header or from <c>hello</c>.</summary>
/// <param name="Value">Credential text; never logged.</param>
public sealed record HelloCredential(string Value);

/// <summary>Result of <see cref="IConnectionAuthenticator"/>.</summary>
/// <param name="Ok">True when the credential is valid.</param>
/// <param name="Principal">Who it belongs to when <paramref name="Ok"/>.</param>
/// <param name="DeviceId">Server-assigned device id; null keeps the id from <c>hello</c>.</param>
/// <param name="ExpiresAt">When the credential stops being valid (the socket closes with 4401), or null.</param>
/// <param name="Reason">Why it failed; logged, never sent to the client.</param>
public sealed record AuthResult(bool Ok, PrincipalRef? Principal, string? DeviceId, DateTimeOffset? ExpiresAt, string? Reason)
{
    /// <summary>A successful result.</summary>
    public static AuthResult Success(PrincipalRef principal, string? deviceId = null, DateTimeOffset? expiresAt = null) =>
        new(true, principal, deviceId, expiresAt, null);

    /// <summary>A failed result.</summary>
    public static AuthResult Fail(string reason) => new(false, null, null, null, reason);
}

/// <summary>Validates the credential of a new connection. The identity module supplies the real one.</summary>
public interface IConnectionAuthenticator
{
    /// <summary>Checks <paramref name="credential"/>; a null credential means none was presented.</summary>
    ValueTask<AuthResult> AuthenticateAsync(HelloCredential? credential, DeviceInfo device, CancellationToken cancellationToken);
}
