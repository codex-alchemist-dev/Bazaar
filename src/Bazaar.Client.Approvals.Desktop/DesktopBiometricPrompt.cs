using Bazaar.DeviceApproval;

namespace Bazaar.Client.Approvals;

/// <summary>
/// <see cref="IBiometricPrompt"/> over a platform <see cref="IPresenceVerifier"/> (on Windows, Windows Hello through
/// <c>UserConsentVerifier</c>, doc 32 section 5a.1). Every call asks afresh and caches nothing. The prompt text names the
/// approval, with the first hex characters of the content hash so it cannot be mistaken for another.
/// </summary>
public sealed class DesktopBiometricPrompt : IBiometricPrompt
{
    private const int _hashPrefixLength = 8;

    private readonly IPresenceVerifier _presence;

    public DesktopBiometricPrompt(IPresenceVerifier presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        _presence = presence;
    }

    /// <inheritdoc />
    public async ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return BiometricOutcome.Cancelled;
        }

        var hash = request.ContentHash.Length > _hashPrefixLength ? request.ContentHash[.._hashPrefixLength] : request.ContentHash;
        var reason = $"{request.Title}: {request.Subtitle} [{hash}]";
        try
        {
            var outcome = await _presence.VerifyAsync(reason, cancellationToken).ConfigureAwait(false);
            return outcome switch
            {
                PresenceOutcome.Verified => BiometricOutcome.Succeeded,
                PresenceOutcome.Cancelled => BiometricOutcome.Cancelled,
                PresenceOutcome.Unavailable => BiometricOutcome.Unavailable,
                _ => BiometricOutcome.Failed,
            };
        }
        catch (OperationCanceledException)
        {
            return BiometricOutcome.Cancelled;
        }
    }
}
