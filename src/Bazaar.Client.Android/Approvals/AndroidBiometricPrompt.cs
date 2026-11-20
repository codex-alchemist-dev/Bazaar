using AndroidX.AppCompat.App;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using Bazaar.Client.Approvals;
using Java.Lang;

namespace Bazaar.Client.Android.Approvals;

/// <summary>
/// The approval biometric prompt over AndroidX BiometricPrompt (doc 29, Approvals). Strong biometric only: no device PIN,
/// pattern or password fallback, and no weak check. Every call shows a new prompt, and the prompt names the approval and the
/// start of its content hash so it cannot be mistaken for another request. A success is returned only from the platform's
/// success callback; every error code goes through <see cref="BiometricErrorMap"/> and none is a success.
/// </summary>
/// <remarks>
/// The binding of the decision to the approval id, the content hash and the decision kind, the single use, and the 30 s grace
/// are the gate's (<see cref="ApprovalGate"/>); this class is the platform prompt only and caches nothing. Dictation never
/// reaches it: the gate accepts only a card button tap. Android-only code: it compiles with the Android workload (BazaarAndroid=true).
/// </remarks>
public sealed class AndroidBiometricPrompt(Func<AppCompatActivity?> activity) : IBiometricPrompt
{
    private const int _shownPrefixLength = 8;

    private readonly Func<AppCompatActivity?> _activity = activity ?? throw new ArgumentNullException(nameof(activity));

    /// <inheritdoc />
    public ValueTask<BiometricOutcome> AuthenticateAsync(BiometricRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var host = _activity();
        if (host is null)
        {
            // No foreground activity: the prompt cannot be shown, so nothing is approved.
            return ValueTask.FromResult(BiometricOutcome.Unavailable);
        }

        if (BiometricManager.From(host).CanAuthenticate(BiometricManager.Authenticators.BiometricStrong) != BiometricManager.BiometricSuccess)
        {
            return ValueTask.FromResult(BiometricOutcome.Unavailable);
        }

        var completion = new TaskCompletionSource<BiometricOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestShort = request.ApprovalId.ToString("N")[.._shownPrefixLength];
        var hashShort = request.ContentHash[..System.Math.Min(_shownPrefixLength, request.ContentHash.Length)];
        host.RunOnUiThread(() =>
        {
            var prompt = new BiometricPrompt(host, ContextCompat.GetMainExecutor(host)!, new Callback(completion));
            var info = new BiometricPrompt.PromptInfo.Builder()
                .SetTitle(request.Title)!
                .SetSubtitle($"{request.Subtitle} (request {requestShort}, content {hashShort})")!
                .SetAllowedAuthenticators(BiometricManager.Authenticators.BiometricStrong)!
                .SetConfirmationRequired(true)!
                .SetNegativeButtonText("Cancel")!
                .Build();
            prompt.Authenticate(info);

            // A cancelled token dismisses the prompt and counts as a cancelled prompt.
            cancellationToken.Register(() => host.RunOnUiThread(() =>
            {
                prompt.CancelAuthentication();
                completion.TrySetResult(BiometricOutcome.Cancelled);
            }));
        });

        return new ValueTask<BiometricOutcome>(completion.Task);
    }

    private sealed class Callback(TaskCompletionSource<BiometricOutcome> completion) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result) =>
            completion.TrySetResult(BiometricOutcome.Succeeded);

        public override void OnAuthenticationError(int errorCode, ICharSequence errString) =>
            completion.TrySetResult(BiometricErrorMap.FromErrorCode(errorCode));

        // One non-matching attempt: the system keeps the prompt open and reports lockout through OnAuthenticationError.
        public override void OnAuthenticationFailed()
        {
        }
    }
}
