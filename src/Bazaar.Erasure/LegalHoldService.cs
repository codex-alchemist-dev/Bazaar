using Bazaar.Approvals;
using Bazaar.Erasure.Abstractions;

namespace Bazaar.Erasure;

/// <summary>
/// The only route to a legal hold change. Checks run in order and the first failure refuses before anything is stored: the
/// scope and reason are usable; the approval is for this action and this exact change; it was signed by the owner who asks;
/// the shared <see cref="IOwnerSignatureVerifier"/> accepts the signature (key, owner, expiry, single-use nonce). A refusal
/// appends nothing. The signing device id is stored with the event.
/// </summary>
public sealed class LegalHoldService(ILegalHoldStore store, IOwnerSignatureVerifier verifier, TimeProvider clock) : ILegalHoldAdmin
{
    public async Task<LegalHoldOutcome> CreateAsync(
        Guid owner, LegalHoldScopeKind kind, string scopeValue, string reason, OwnerHoldApproval? approval, CancellationToken ct = default)
    {
        if (owner == Guid.Empty || !TryNormalise(kind, scopeValue, out var scope) || !ReasonOk(reason))
            return LegalHoldOutcome.Refused(LegalHoldRefusal.Invalid);
        reason = reason.Trim();

        var refusal = await CheckAsync(owner, approval, LegalHoldArgs.CreateAction, LegalHoldArgs.CreateHash(kind, scope, reason), ct);
        if (refusal is { } r)
            return LegalHoldOutcome.Refused(r);

        var holdId = Guid.NewGuid();
        await store.CreateAsync(holdId, kind, scope, reason, owner.ToString("D"), approval!.Payload.DeviceId, clock.GetUtcNow(), ct);
        return LegalHoldOutcome.Done(holdId);
    }

    public async Task<LegalHoldOutcome> ReleaseAsync(Guid owner, Guid holdId, string reason, OwnerHoldApproval? approval, CancellationToken ct = default)
    {
        if (owner == Guid.Empty || holdId == Guid.Empty || !ReasonOk(reason))
            return LegalHoldOutcome.Refused(LegalHoldRefusal.Invalid);
        reason = reason.Trim();

        var refusal = await CheckAsync(owner, approval, LegalHoldArgs.ReleaseAction, LegalHoldArgs.ReleaseHash(holdId), ct);
        if (refusal is { } r)
            return LegalHoldOutcome.Refused(r);

        return await store.ReleaseAsync(holdId, reason, owner.ToString("D"), approval!.Payload.DeviceId, clock.GetUtcNow(), ct)
            ? LegalHoldOutcome.Done(holdId)
            : LegalHoldOutcome.Refused(LegalHoldRefusal.NotActive);
    }

    public Task<IReadOnlyList<LegalHold>> ListActiveAsync(CancellationToken ct = default) => store.ListActiveAsync(ct);

    private async Task<LegalHoldRefusal?> CheckAsync(Guid owner, OwnerHoldApproval? approval, string action, byte[] argsHash, CancellationToken ct)
    {
        if (approval is null)
            return LegalHoldRefusal.NotApproved;

        var payload = approval.Payload;
        if (payload.Action != action || payload.HumanId != owner || !payload.ArgsHash.AsSpan().SequenceEqual(argsHash))
            return LegalHoldRefusal.NotApproved;

        return await verifier.VerifyAsync(payload, approval.Signature, clock.GetUtcNow(), ct) == OwnerSignatureVerdict.Valid
            ? null
            : LegalHoldRefusal.SignatureRefused;
    }

    private static bool ReasonOk(string reason) =>
        !string.IsNullOrWhiteSpace(reason) && reason.Trim().Length <= LegalHoldArgs.MaxReasonLength;

    /// <summary>A person hold names a person id (stored lower-case and hyphenated, as the reader compares it); other scopes need a non-blank value.</summary>
    private static bool TryNormalise(LegalHoldScopeKind kind, string value, out string normalised)
    {
        normalised = string.Empty;
        if (!Enum.IsDefined(kind) || string.IsNullOrWhiteSpace(value))
            return false;

        if (kind == LegalHoldScopeKind.Person)
        {
            if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
                return false;
            normalised = id.ToString("D");
            return true;
        }

        normalised = value.Trim();
        return true;
    }
}
