using System.Runtime.Versioning;
using Windows.Security.Credentials.UI;

namespace Bazaar.DeviceApproval.Windows;

/// <summary>
/// Windows Hello presence for one approval (docs/design/32, 5a.1). Each call asks the person afresh; nothing is cached,
/// so one approval never carries over to the next. The prompt is shown on the owner's screen only when an approval is
/// being signed.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsHelloPresence : IPresenceVerifier
{
    /// <inheritdoc />
    public async ValueTask<PresenceOutcome> VerifyAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        cancellationToken.ThrowIfCancellationRequested();

        var availability = await UserConsentVerifier.CheckAvailabilityAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (availability != UserConsentVerifierAvailability.Available)
        {
            return PresenceOutcome.Unavailable;
        }

        var result = await UserConsentVerifier.RequestVerificationAsync(reason).AsTask(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            UserConsentVerificationResult.Verified => PresenceOutcome.Verified,
            UserConsentVerificationResult.Canceled => PresenceOutcome.Cancelled,
            UserConsentVerificationResult.DeviceBusy
                or UserConsentVerificationResult.DeviceNotPresent
                or UserConsentVerificationResult.DisabledByPolicy
                or UserConsentVerificationResult.NotConfiguredForUser
                or UserConsentVerificationResult.RetriesExhausted => PresenceOutcome.Unavailable,
            _ => PresenceOutcome.Failed,
        };
    }
}
