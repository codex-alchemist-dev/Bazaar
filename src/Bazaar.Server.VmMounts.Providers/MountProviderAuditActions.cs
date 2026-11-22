namespace Bazaar.Server.VmMounts.Providers;

/// <summary>Audit kinds this module writes beyond <see cref="VmMountAuditActions"/>.</summary>
public static class MountProviderAuditActions
{
    /// <summary>A canary use was received by the sink. Written first, before containment is attempted.</summary>
    public const string CanaryTrip = "vm.mount.canary_trip";

    /// <summary>Containment of the agent succeeded after a canary use.</summary>
    public const string CanaryContained = "vm.mount.canary_contained";

    /// <summary>Containment of the agent failed after a canary use; it is retried until it succeeds.</summary>
    public const string CanaryContainmentFailed = "vm.mount.canary_containment_failed";

    /// <summary>A stale SMB share was removed at reconcile.</summary>
    public const string StaleShareRemoved = "vm.mount.stale_share_removed";
}
