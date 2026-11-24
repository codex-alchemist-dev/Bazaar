using System.Runtime.Versioning;
using Bazaar.Client.Approvals;
using Bazaar.Client.GitGui.Merge;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.DeviceApproval;
using Bazaar.DeviceApproval.Windows;

namespace Bazaar.Client.Windows;

/// <summary>
/// What the head needs from the platform to approve a merge: whether the approval key is enrolled, a signer over it, and a
/// presence check. The Windows implementation is <see cref="WindowsDeviceApprovalPlatform"/>; tests use a fake.
/// </summary>
public interface IDeviceApprovalPlatform
{
    /// <summary>True when the approval key already exists on this device. Never creates one.</summary>
    bool HasEnrolledKey(string keyName);

    /// <summary>Opens the signer over the enrolled key. The caller disposes it.</summary>
    IApprovalSigner OpenSigner(string keyName);

    /// <summary>The fresh-presence check (Windows Hello).</summary>
    IPresenceVerifier Presence { get; }
}

/// <summary>The TPM key and Windows Hello (doc 32 section 5a.1), behind <see cref="IDeviceApprovalPlatform"/>.</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsDeviceApprovalPlatform : IDeviceApprovalPlatform
{
    /// <inheritdoc />
    public IPresenceVerifier Presence { get; } = new WindowsHelloPresence();

    /// <summary>The platform on Windows 10 2004 or newer; null anywhere else, so the head builds and runs on any OS.</summary>
    public static IDeviceApprovalPlatform? TryCreate() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ? new WindowsDeviceApprovalPlatform() : null;

    /// <inheritdoc />
    public bool HasEnrolledKey(string keyName) => TpmApprovalSigner.KeyExists(keyName);

    /// <inheritdoc />
    public IApprovalSigner OpenSigner(string keyName) => new TpmApprovalSigner(keyName);
}

/// <summary>Registers the merge approver (doc 52, Client; doc 32 section 5a.1).</summary>
public static class DeviceApprovalComposition
{
    /// <summary>
    /// The one approval key name on this device (doc 32 section 5a: the <c>owner-approval</c> key). A constant, so enrolment (doc 32
    /// section 5b) and the merge path name the same key.
    /// </summary>
    public const string ApprovalKeyName = "Bazaar.OwnerApproval";

    /// <summary>
    /// Injects <see cref="IOwnerMergeApprover"/> when this platform can approve at all, a person is signed in
    /// (<paramref name="identity"/>) and the sign-in handoff gave a device id (<paramref name="deviceId"/>). Otherwise it adds nothing
    /// and the merge card says this client cannot approve. A registered approver checks for the enrolled key on every approval and
    /// answers <see cref="MergeApprovalOutcome.Unavailable"/> when there is none; it never creates a key.
    /// </summary>
    public static PaneModuleContext WithOwnerMergeApprover(
        this PaneModuleContext context,
        IDeviceApprovalPlatform? platform,
        IOwnerMergeIdentity? identity,
        Guid? deviceId,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (platform is null || identity is null || deviceId is null || deviceId.Value == Guid.Empty)
        {
            return context;
        }

        return context.With<IOwnerMergeApprover>(new EnrolledKeyMergeApprover(platform, identity, deviceId.Value.ToString("D"), clock));
    }

    /// <summary>
    /// Builds the real approver on each call, so a key enrolled while the app runs is picked up, and answers Unavailable
    /// while there is none.
    /// </summary>
    private sealed class EnrolledKeyMergeApprover(IDeviceApprovalPlatform platform, IOwnerMergeIdentity identity, string deviceId, TimeProvider? clock)
        : IOwnerMergeApprover
    {
        public async Task<MergeApprovalResult> ApproveAsync(MergeApprovalRequest request, CancellationToken cancellationToken)
        {
            if (!platform.HasEnrolledKey(ApprovalKeyName))
            {
                return new MergeApprovalResult(MergeApprovalOutcome.Unavailable, null);
            }

            IApprovalSigner signer;
            try
            {
                signer = platform.OpenSigner(ApprovalKeyName);
            }
            catch (ApprovalRefusedException)
            {
                return new MergeApprovalResult(MergeApprovalOutcome.Unavailable, null);
            }

            try
            {
                var approver = new DeviceOwnerMergeApprover(
                    new DesktopBiometricPrompt(platform.Presence), new DesktopDeviceApprovalSigner(deviceId, signer), identity, clock);
                return await approver.ApproveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                (signer as IDisposable)?.Dispose();
            }
        }
    }
}
