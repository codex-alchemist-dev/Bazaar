namespace Bazaar.Client.Approvals;

/// <summary>How one biometric prompt ended. Only <see cref="Succeeded"/> lets an approval go on (doc 29, Approvals).</summary>
public enum BiometricOutcome
{
    /// <summary>The person passed a fresh biometric check.</summary>
    Succeeded,

    /// <summary>The person dismissed the prompt.</summary>
    Cancelled,

    /// <summary>The check ran and did not match, or the platform locked it out.</summary>
    Failed,

    /// <summary>No biometric is enrolled or usable on this device. There is no fallback to a weaker check.</summary>
    Unavailable,
}

/// <summary>What the prompt tells the person. It names the approval it is for, so the prompt cannot be mistaken for another.</summary>
/// <param name="ApprovalId">Approval this prompt is for.</param>
/// <param name="ContentHash">Lowercase hex of the payload hash on the card the person is looking at.</param>
/// <param name="Title">Prompt title, set by the gate.</param>
/// <param name="Subtitle">Prompt subtitle, set by the gate (agent and tool).</param>
public sealed record BiometricRequest(Guid ApprovalId, string ContentHash, string Title, string Subtitle);

/// <summary>
/// Asks the person for a fresh biometric check. The Android head implements it over BiometricPrompt; tests use a fake.
/// An implementation must prompt every time: it never caches a result and never returns <see cref="BiometricOutcome.Succeeded"/>
/// for a device credential or a timeout.
/// </summary>
public interface IBiometricPrompt
{
    /// <summary>Shows the prompt and returns how it ended. Cancellation of <paramref name="cancellationToken"/> counts as a cancelled prompt.</summary>
    ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken);
}
