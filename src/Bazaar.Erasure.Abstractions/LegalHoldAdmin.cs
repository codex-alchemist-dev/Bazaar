using System.Security.Cryptography;
using System.Text;
using Bazaar.Approvals;

namespace Bazaar.Erasure.Abstractions;

/// <summary>What a legal hold covers (doc 48 section 9). Only <see cref="Person"/> holds are enforced today; the rest are recorded.</summary>
public enum LegalHoldScopeKind
{
    Person,
    Gc,
    Company,
    Category,
}

/// <summary>One active legal hold.</summary>
public sealed record LegalHold(Guid HoldId, LegalHoldScopeKind ScopeKind, string ScopeValue, string Reason, string Actor, DateTimeOffset CreatedAt);

/// <summary>The owner's signed approval of one hold change (doc 48 section 9, doc 32 section 5a), in the shared owner payload shape.</summary>
public sealed record OwnerHoldApproval(OwnerPayload Payload, byte[] Signature);

/// <summary>Why a hold change was refused. A refusal changes nothing.</summary>
public enum LegalHoldRefusal
{
    /// <summary>No approval, or its action, signer or args do not match this change.</summary>
    NotApproved,

    /// <summary>The shared verifier refused the signature (key, owner, expiry or replayed nonce).</summary>
    SignatureRefused,

    /// <summary>The scope or the reason is not usable.</summary>
    Invalid,

    /// <summary>The hold to release does not exist or is already released.</summary>
    NotActive,
}

/// <summary>The result of a hold change: the hold id on success, otherwise the refusal.</summary>
public readonly record struct LegalHoldOutcome(Guid HoldId, LegalHoldRefusal? Refusal)
{
    public bool Succeeded => Refusal is null;

    public static LegalHoldOutcome Done(Guid holdId) => new(holdId, null);

    public static LegalHoldOutcome Refused(LegalHoldRefusal refusal) => new(Guid.Empty, refusal);
}

/// <summary>The action names and args hashes the owner's device signs for a hold change.</summary>
public static class LegalHoldArgs
{
    public const string CreateAction = "legal_hold.create";
    public const string ReleaseAction = "legal_hold.release";

    /// <summary>The longest reason the log accepts.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>SHA-256 over the scope and the reason of a new hold, each field length-prefixed.</summary>
    public static byte[] CreateHash(LegalHoldScopeKind kind, string scopeValue, string reason) =>
        Hash("create", kind.ToString().ToLowerInvariant(), scopeValue, reason);

    /// <summary>SHA-256 over the hold being released.</summary>
    public static byte[] ReleaseHash(Guid holdId) => Hash("release", holdId.ToString("D"));

    private static byte[] Hash(params string[] fields)
    {
        var sb = new StringBuilder();
        foreach (var f in fields)
            sb.Append(f.Length).Append(':').Append(f).Append(';');
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}

/// <summary>
/// Creates and releases legal holds. Both need the owner's device-key signature, verified through the shared
/// <see cref="IOwnerSignatureVerifier"/> with action <see cref="LegalHoldArgs.CreateAction"/> or
/// <see cref="LegalHoldArgs.ReleaseAction"/> (doc 48 section 9, owner card 2026-11-16). Biometrics layer on when they ship (section 8).
/// </summary>
public interface ILegalHoldAdmin
{
    Task<LegalHoldOutcome> CreateAsync(Guid owner, LegalHoldScopeKind kind, string scopeValue, string reason, OwnerHoldApproval? approval, CancellationToken ct = default);

    Task<LegalHoldOutcome> ReleaseAsync(Guid owner, Guid holdId, string reason, OwnerHoldApproval? approval, CancellationToken ct = default);

    Task<IReadOnlyList<LegalHold>> ListActiveAsync(CancellationToken ct = default);
}
