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
