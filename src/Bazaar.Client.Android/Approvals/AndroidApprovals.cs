using Avalonia.Controls;
using AndroidX.AppCompat.App;
using Bazaar.Client.Approvals;
using Bazaar.Client.Approvals.Avalonia;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Android.Approvals;

/// <summary>
/// Composes the phone's approval path (doc 29, Approvals): the biometric prompt, the device signer and the gate. An approval
/// card built here decides only through <see cref="Gate"/>.
/// </summary>
/// <remarks>
/// Enrolment of the approval key is undecided (doc 32 Question 4), so the signer is <see cref="NotEnrolledApprovalSigner"/>:
/// the biometric prompt is not shown and nothing is signed or sent. There is also no wire type for a signed decision yet
/// (doc 02), so the sender is <see cref="UnroutedApprovalDecisionSender"/>. Both are the seams to replace once those are decided.
/// </remarks>
public sealed class AndroidApprovals
{
    private readonly NotEnrolledApprovalSigner _signer = new();

    /// <param name="activity">The running activity, read at each prompt.</param>
    public AndroidApprovals(Func<AppCompatActivity?> activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Gate = new ApprovalGate(
            new SignerReadyBiometricPrompt(new AndroidBiometricPrompt(activity), () => _signer.IsEnrolled),
            _signer,
            new UnroutedApprovalDecisionSender());
    }

    /// <summary>The one gate every approval card on this phone decides through.</summary>
    public ApprovalGate Gate { get; }

    /// <summary>True when this device can sign a decision. False until enrolment exists.</summary>
    public bool IsEnrolled => _signer.IsEnrolled;

    /// <summary>An approval card for <paramref name="card"/>, wired to <see cref="Gate"/>.</summary>
    public Control CreateCardView(ApprovalCardDto card) =>
        new ApprovalCardView { DataContext = new ApprovalCardViewModel(Gate, card) };
}
