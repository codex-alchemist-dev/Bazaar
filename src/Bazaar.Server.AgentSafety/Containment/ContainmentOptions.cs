namespace Bazaar.Server.AgentSafety.Containment;

/// <summary>What a guest scrub does to the VM (see <see cref="GuestScrubOptions"/>).</summary>
public enum GuestScrubAction
{
    /// <summary>Force-stop the VM if it is running and keep its disk. Reversible: the owner can inspect or resume it. Scrubbing is deferred.</summary>
    PauseOnly,

    /// <summary>Force-stop the VM, then retire it, which discards its disk. IRREVERSIBLE: whatever the guest held is gone, and a fresh VM is provisioned from the parent image.</summary>
    Retire,
}

/// <summary>
/// What the guest scrubber does per detach reason. Defaults: a hard taint retires the VM (the guest may hold a profile copy an attacker
/// reached), the kill switch only pauses it (an owner-pressed stop must not destroy work by itself). Retiring is irreversible; the
/// split is an owner question and an operator setting.
/// </summary>
public sealed class GuestScrubOptions
{
    /// <summary>Action after a hard-taint detach. Default <see cref="GuestScrubAction.Retire"/>.</summary>
    public GuestScrubAction HardTaint { get; set; } = GuestScrubAction.Retire;

    /// <summary>Action after a kill-switch detach. Default <see cref="GuestScrubAction.PauseOnly"/>.</summary>
    public GuestScrubAction KillSwitch { get; set; } = GuestScrubAction.PauseOnly;
}

/// <summary>Settings of the containment decorator.</summary>
public sealed class ContainmentOptions
{
    /// <summary>
    /// Upper bound for each containment leg (token revoke, egress block, mount detach, inner contain, egress release). Legs ignore the
    /// caller's cancellation, so a dropped request cannot leave an agent half contained; this timeout is what ends a hung leg.
    /// </summary>
    public TimeSpan LegTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
