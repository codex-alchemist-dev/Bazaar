using Bazaar.Roles.Abstractions;
using Bazaar.Server.Biometric;

namespace Bazaar.Roles.Biometric;

/// <summary>
/// Layers the device biometric on top of the owner's signature for a grant widening (doc 48 sections 4 and 8.1). The assertion
/// must verify against the same owner payload the signature covers, as a high-risk action, so the factor allow-list and the
/// liveness floor apply. No assertion, or any verdict other than <see cref="BiometricVerdict.Valid"/>, refuses.
/// </summary>
public sealed class BiometricWideningCheck(IBiometricAssertionVerifier verifier, TimeProvider clock) : IWideningBiometricCheck
{
    public async Task<bool> PassedAsync(GrantWideningRequest request, OwnerWideningApproval approval, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Biometric is not { } assertion)
            return false;

        return await verifier.VerifyAsync(assertion, approval.Payload, highRisk: true, clock.GetUtcNow(), ct) == BiometricVerdict.Valid;
    }
}
