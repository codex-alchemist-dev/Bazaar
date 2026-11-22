using Bazaar.Approvals;
using Bazaar.Roles.Abstractions;

namespace Bazaar.Roles;

/// <summary>
/// Approves a widening only when the actor is a human who carries the owner's signed approval of this exact change (doc 48
/// section 4, doc 32 section 5a). The checks run in order and the first failure refuses: the actor is a person; the approval
/// is for <see cref="WideningArgs.Action"/>; it was signed by that person; it binds this role, person and grant set; the shared
/// <see cref="IOwnerSignatureVerifier"/> accepts the signature (key, owner, expiry, single-use nonce); and every registered
/// <see cref="IWideningBiometricCheck"/> passes.
/// </summary>
public sealed class OwnerSignedWideningGate(
    IOwnerSignatureVerifier verifier,
    TimeProvider clock,
    IEnumerable<IWideningBiometricCheck> biometrics) : IGrantWideningGate
{
    public async Task<bool> ApproveAsync(GrantWideningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Actor.Kind != PrincipalKind.Human || request.Actor.Approval is not { } approval)
            return false;

        var payload = approval.Payload;
        if (payload.Action != WideningArgs.Action
            || payload.HumanId != request.Actor.PrincipalId
            || !payload.ArgsHash.AsSpan().SequenceEqual(WideningArgs.Hash(request)))
            return false;

        if (await verifier.VerifyAsync(payload, approval.Signature, clock.GetUtcNow(), ct) != OwnerSignatureVerdict.Valid)
            return false;

        foreach (var check in biometrics)
        {
            if (!await check.PassedAsync(request, approval, ct))
                return false;
        }

        return true;
    }
}
