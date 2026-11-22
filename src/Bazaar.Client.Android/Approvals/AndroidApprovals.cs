using Android.App;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Approvals;
using Bazaar.Client.Approvals.Http;

namespace Bazaar.Client.Android.Approvals;

/// <summary>Builds what the approvals pane needs on this phone: the biometric gate and the server source (doc 29 Approvals).</summary>
public static class AndroidApprovals
{
    /// <summary>
    /// The gate and the pending-approvals source, or <c>(null, source)</c> when no approval key is enrolled yet, so the pane
    /// says it cannot decide. <paramref name="enrolledDeviceId"/> is the key id the server knows, from enrolment (doc 32).
    /// </summary>
    public static (ApprovalGate? Gate, IPendingApprovalSource Source) Create(ServerEndpoint endpoint, Func<Activity?> activity, string? enrolledDeviceId)
    {
        var client = HttpApprovalClient.Create(endpoint);
        if (string.IsNullOrEmpty(enrolledDeviceId) || !KeystoreDeviceApprovalSigner.HasKey())
        {
            return (null, client);
        }

        var gate = new ApprovalGate(new AndroidBiometricPrompt(activity), new KeystoreDeviceApprovalSigner(enrolledDeviceId), client);
        return (gate, client);
    }
}
