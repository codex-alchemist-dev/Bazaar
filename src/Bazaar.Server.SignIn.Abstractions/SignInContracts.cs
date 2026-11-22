using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.SignIn;

/// <summary>The consumers a handoff code can be issued for (doc 32 section 4). A code redeems only for the audience it was issued for.</summary>
public static class SignInAudiences
{
    /// <summary>The admin web panel (<c>/admin/session/redeem</c>, doc 51).</summary>
    public const string Admin = "admin";

    /// <summary>A native Bazaar client (<c>bazaar://signin</c>, redeemed at <c>/auth/handoff</c>).</summary>
    public const string Native = "native";

    public static bool IsKnown(string? audience) =>
        string.Equals(audience, Admin, StringComparison.Ordinal) || string.Equals(audience, Native, StringComparison.Ordinal);
}

/// <summary>Names of the rate-limit buckets the sign-in routes belong to. Enforcement is the rate-limit module's job.</summary>
public static class SignInRateLimitBuckets
{
    public const string SignIn = "signin";
}

/// <summary>A principal that proved possession of a passkey, and the device the passkey belongs to.</summary>
public sealed record SignInIdentity(Guid PrincipalId, PrincipalKind Kind, Guid DeviceId);

/// <summary>A freshly issued WebAuthn challenge. <see cref="Id"/> is the handle the page sends back; <see cref="Challenge"/> is base64url.</summary>
public sealed record PasskeyChallenge(string Id, string Challenge, DateTimeOffset ExpiresAt);

/// <summary>
/// Stores the 60 s, single-use handoff codes (doc 32 sections 4 and 5). Only the SHA-256 hash of a code is kept. A code is bound to
/// the audience it was issued for: redeeming it for another audience fails and burns it.
/// </summary>
public interface IHandoffCodeStore
{
    /// <summary>Issues a code for <paramref name="audience"/> (a <see cref="SignInAudiences"/> value). The code is returned once and never stored.</summary>
    Task<string> IssueAsync(SignInIdentity identity, string audience, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Returns the identity and removes the code, or null when it is unknown, used, expired or issued for another audience.</summary>
    Task<SignInIdentity?> RedeemAsync(string code, string audience, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>Issues and consumes WebAuthn challenges. A challenge is single use and short lived.</summary>
public interface IPasskeyChallengeStore
{
    Task<PasskeyChallenge> IssueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Returns the base64url challenge for <paramref name="id"/> and removes it, or null when unknown, used or expired.</summary>
    Task<string?> ConsumeAsync(string id, DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
/// Port for WebAuthn assertion verification. The sign-in module does no CBOR or signature work itself: an adapter over the passkey
/// library owns that. Null means the assertion was not valid for any known passkey, with no reason given.
/// </summary>
public interface IPasskeyAssertionVerifier
{
    /// <param name="challenge">The base64url challenge the assertion must answer.</param>
    /// <param name="assertionJson">The assertion as the page serialised it (the credential response fields, base64url encoded).</param>
    Task<SignInIdentity?> VerifyAsync(string challenge, string assertionJson, CancellationToken ct = default);
}
