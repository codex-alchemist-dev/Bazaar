using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals;

/// <summary>
/// Maps the integer error codes of the platform biometric prompt (Android's <c>BiometricPrompt.ERROR_*</c>, which are stable
/// public constants) to the one outcome the gate understands. Only a success callback is <see cref="BiometricOutcome.Succeeded"/>;
/// no error code is ever a success, and an unknown code is a failure, never a pass.
/// </summary>
public static class BiometricErrorMap
{
    // BiometricPrompt.ERROR_CANCELED, ERROR_USER_CANCELED and ERROR_NEGATIVE_BUTTON: the person or the system dismissed the prompt.
    private const int _canceled = 5;
    private const int _userCanceled = 10;
    private const int _negativeButton = 13;

    // ERROR_HW_UNAVAILABLE, ERROR_NO_BIOMETRICS, ERROR_HW_NOT_PRESENT, ERROR_NO_DEVICE_CREDENTIAL: nothing usable to check against.
    private const int _hardwareUnavailable = 1;
    private const int _noBiometrics = 11;
    private const int _hardwareNotPresent = 12;
    private const int _noDeviceCredential = 14;

    /// <summary>The outcome for an error callback with this code.</summary>
    public static BiometricOutcome FromErrorCode(int errorCode) => errorCode switch
    {
        _canceled or _userCanceled or _negativeButton => BiometricOutcome.Cancelled,
        _hardwareUnavailable or _noBiometrics or _hardwareNotPresent or _noDeviceCredential => BiometricOutcome.Unavailable,
        // Lockout, timeout, vendor and unknown codes: the check did not pass.
        _ => BiometricOutcome.Failed,
    };
}

/// <summary>Thrown by <see cref="NotEnrolledApprovalSigner"/> when asked to sign: this device has no approval key.</summary>
public sealed class ApprovalKeyNotEnrolledException() : InvalidOperationException("This device has no enrolled approval key, so it cannot sign a decision.");

/// <summary>
/// The signer for a device whose approval key is not enrolled (doc 32 section 5a; the enrolment path is doc 32 Question 4 and is
/// not decided). It has the shape the gate needs and signs nothing: <see cref="SignAsync"/> always throws
/// <see cref="ApprovalKeyNotEnrolledException"/>, which the gate reports as a refused decision with nothing sent.
/// Swap it for a real signer only when enrolment exists; nothing here creates a key.
/// </summary>
public sealed class NotEnrolledApprovalSigner : IDeviceApprovalSigner
{
    /// <summary>A placeholder with a valid shape, so the gate can be built. The server knows no such device.</summary>
    public const string PlaceholderDeviceId = "not-enrolled";

    /// <inheritdoc />
    public string DeviceId => PlaceholderDeviceId;

    /// <summary>Always false. Heads read this to say "not enrolled" instead of offering a decision that cannot be signed.</summary>
    public bool IsEnrolled { get; }

    /// <inheritdoc />
    public ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
        ValueTask.FromException<byte[]>(new ApprovalKeyNotEnrolledException());
}

/// <summary>
/// Wraps a biometric prompt so it is shown only when a decision could actually be signed. With no enrolled key the person is
/// never asked for a fingerprint for a decision that must fail: the wrapper answers <see cref="BiometricOutcome.Unavailable"/>
/// without prompting. An approve or deny therefore sends nothing and the card says so.
/// </summary>
/// <param name="inner">The platform prompt.</param>
/// <param name="canSign">True when the device has an enrolled approval key. Read at each prompt, so enrolment takes effect at once.</param>
public sealed class SignerReadyBiometricPrompt(IBiometricPrompt inner, Func<bool> canSign) : IBiometricPrompt
{
    private readonly IBiometricPrompt _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly Func<bool> _canSign = canSign ?? throw new ArgumentNullException(nameof(canSign));

    /// <inheritdoc />
    public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken) =>
        _canSign() ? _inner.AuthenticateAsync(request, cancellationToken) : ValueTask.FromResult(BiometricOutcome.Unavailable);
}

/// <summary>
/// The sender for a build whose server has no decision route yet (no wire type exists for signed approval decisions, doc 02).
/// It always throws, so the gate reports a failed send and never retries; no decision leaves the phone silently.
/// </summary>
public sealed class UnroutedApprovalDecisionSender : IApprovalDecisionSender
{
    /// <inheritdoc />
    public ValueTask SendAsync(ApprovalDecisionDto decision, CancellationToken cancellationToken) =>
        ValueTask.FromException(new NotSupportedException("The server has no route for signed approval decisions yet."));
}
