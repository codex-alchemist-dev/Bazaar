using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
using Bazaar.Client.Approvals;

namespace Bazaar.Client.Android.Approvals;

/// <summary>
/// The Android fresh-biometric prompt for approvals (doc 29 Approvals). It uses the platform BiometricPrompt with a strong
/// biometric only: no device PIN or password fallback, and no cached result, so every approval prompts. Every outcome
/// other than a recognised biometric maps to a refusal. UNVERIFIED on device: this head cannot be built in CI.
/// </summary>
public sealed class AndroidBiometricPrompt(Func<Activity?> activity) : IBiometricPrompt
{
    public async ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = activity();
        if (current is null)
        {
            return BiometricOutcome.Unavailable;
        }

        var manager = (BiometricManager?)current.GetSystemService(Context.BiometricService);
        if (manager is null || manager.CanAuthenticate((int)BiometricManager.Authenticators.BiometricStrong) != (int)BiometricCode.Success)
        {
            return BiometricOutcome.Unavailable;
        }

        var done = new TaskCompletionSource<BiometricOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = current.MainExecutor!;
        var cancel = new CancellationSignal();
        var prompt = new BiometricPrompt.Builder(current)
            .SetTitle(request.Title)!
            .SetSubtitle(request.Subtitle)!
            .SetAllowedAuthenticators((int)BiometricManager.Authenticators.BiometricStrong)!
            .SetNegativeButton("Cancel", executor, new CancelListener(done))!
            .Build()!;

        using var registration = cancellationToken.Register(() =>
        {
            cancel.Cancel();
            done.TrySetResult(BiometricOutcome.Cancelled);
        });
        prompt.Authenticate(cancel, executor, new Callback(done));
        return await done.Task.ConfigureAwait(false);
    }

    private sealed class CancelListener(TaskCompletionSource<BiometricOutcome> done) : Java.Lang.Object, global::Android.Content.IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) => done.TrySetResult(BiometricOutcome.Cancelled);
    }

    private sealed class Callback(TaskCompletionSource<BiometricOutcome> done) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) => done.TrySetResult(BiometricOutcome.Succeeded);

        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            done.TrySetResult(errorCode switch
            {
                BiometricErrorCode.UserCanceled or BiometricErrorCode.Canceled => BiometricOutcome.Cancelled,
                BiometricErrorCode.HwNotPresent or BiometricErrorCode.HwUnavailable or BiometricErrorCode.NoBiometrics => BiometricOutcome.Unavailable,
                _ => BiometricOutcome.Failed,
            });

        // A non-matching attempt: the prompt stays open and the person can retry, so this is not a result.
        public override void OnAuthenticationFailed()
        {
        }
    }
}
