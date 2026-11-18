namespace Bazaar.Server.VmMounts;

/// <summary>The audit kinds the mount manager writes (doc 15 "Audit": mount events).</summary>
public static class VmMountAuditActions
{
    public const string Actor = "vm-mounts";

    /// <summary>A mount was attached.</summary>
    public const string Attach = "vm.mount.attach";

    /// <summary>An attach was refused by a check. Nothing was attached.</summary>
    public const string Refused = "vm.mount.refused";

    /// <summary>A mount was detached, with the reason and the write-back outcome.</summary>
    public const string Detach = "vm.mount.detach";

    /// <summary>The provider failed to detach; the mount row stays attached.</summary>
    public const string DetachFailed = "vm.mount.detach_failed";

    /// <summary>A containment detach could not scrub the guest (<see cref="Abstractions.IGuestScrubber"/> failed or is not wired).</summary>
    public const string ScrubFailed = "vm.mount.scrub_failed";

    /// <summary>The guest cookie profile was written to the host profile.</summary>
    public const string WriteBack = "vm.mount.writeback";

    /// <summary>The guest cookie profile failed a hash check or validation and was not written.</summary>
    public const string WriteBackRejected = "vm.mount.writeback_rejected";

    /// <summary>A planted canary was read or its value was seen outside the guest.</summary>
    public const string CanaryUse = "vm.mount.canary_use";
}
