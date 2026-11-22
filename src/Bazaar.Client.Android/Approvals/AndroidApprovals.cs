using Android.App;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Client.Approvals;
using Bazaar.Client.Approvals.Http;
using Bazaar.Client.OwnerId;
using Bazaar.Client.TalentPool;
using Bazaar.Client.TalentPool.Http;

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

    /// <summary>
    /// The Talent tool-grant gate and source (docs/design/25), over the same biometric and Keystore key as approvals. The gate is
    /// null until an approval key and the owner's human id are enrolled, so the pane lists nothing it could not sign.
    /// <paramref name="ownerHumanId"/> is the owner's id the server knows from enrolment (doc 32).
    /// </summary>
    public static (ToolGrantGate? Gate, IPendingToolGrantSource Source) CreateToolGrants(
        ServerEndpoint endpoint, Func<Activity?> activity, string? enrolledDeviceId, string? ownerHumanId)
    {
        var client = HttpToolGrantClient.Create(endpoint);
        if (string.IsNullOrEmpty(enrolledDeviceId) || !Guid.TryParse(ownerHumanId, out var human) || !KeystoreDeviceApprovalSigner.HasKey())
        {
            return (null, client);
        }

        return (new ToolGrantGate(new AndroidBiometricPrompt(activity), new KeystoreDeviceApprovalSigner(enrolledDeviceId), client, human), client);
    }

    /// <summary>
    /// Stores the owner's human id (a GUID the server knows) next to the approval device id. It creates no key: the Keystore key and
    /// its enrolment stay with doc 32 Question 4. Returns false for text that is not a GUID, so nothing malformed is kept.
    /// </summary>
    public static bool SaveOwnerHumanId(Activity? activity, string? ownerHumanId)
    {
        if (activity is null || !Guid.TryParse(ownerHumanId, out var id))
        {
            return false;
        }

        var prefs = activity.GetSharedPreferences("bazaar", global::Android.Content.FileCreationMode.Private);
        prefs?.Edit()?.PutString("owner_human_id", id.ToString("D"))?.Apply();
        return prefs is not null;
    }
}

/// <summary>The owner id store over this app's private preferences (doc 32). It stores an id only and creates no key.</summary>
public sealed class AndroidOwnerIdStore(Func<Activity?> activity) : IOwnerIdStore
{
    public string? Load() =>
        activity()?.GetSharedPreferences("bazaar", global::Android.Content.FileCreationMode.Private)?.GetString("owner_human_id", null);

    public bool Save(string canonicalId) => AndroidApprovals.SaveOwnerHumanId(activity(), canonicalId);
}
