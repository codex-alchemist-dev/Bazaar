namespace Bazaar.Server.VmDensity.Abstractions;

/// <summary>The guest operating system of a VM. The governor saves idle Windows VMs before idle Linux VMs (doc 59 section 8).</summary>
public enum DensityGuestOs
{
    /// <summary>A Windows guest.</summary>
    Windows,

    /// <summary>A Linux guest.</summary>
    Linux,
}

/// <summary>A VM the governor is asked to admit or may save: identity, owner agent, guest OS and assigned memory.</summary>
/// <param name="VmId">The VM's id.</param>
/// <param name="AgentId">The owning agent's id.</param>
/// <param name="Os">The guest OS.</param>
/// <param name="MemoryMb">Memory assigned to the VM, in MiB.</param>
public sealed record DensityVm(Guid VmId, string AgentId, DensityGuestOs Os, int MemoryMb);

/// <summary>What a running VM is doing right now. A busy VM is never chosen to be saved.</summary>
/// <param name="HasRunningExec">An exec is running in the guest.</param>
/// <param name="HasOpenTerminal">A terminal session is open.</param>
/// <param name="HasBrowserSession">A browser session is live.</param>
/// <param name="LastUsedAt">When the VM was last used.</param>
/// <param name="IsUnknown">
/// The activity could not be read (PROPOSED, doc 59 section 8). An unknown activity counts as busy, so the VM is never saved on a
/// guess. Added after the first four members; existing callers keep compiling.
/// </param>
public sealed record VmActivity(bool HasRunningExec, bool HasOpenTerminal, bool HasBrowserSession, DateTimeOffset LastUsedAt, bool IsUnknown = false)
{
    /// <summary>True when the activity is unknown, or an exec, a terminal or a browser session is active.</summary>
    public bool IsBusy => IsUnknown || HasRunningExec || HasOpenTerminal || HasBrowserSession;

    /// <summary>The activity of a VM whose activity could not be read: busy, last used at <paramref name="now"/>.</summary>
    /// <param name="now">The current time.</param>
    public static VmActivity Unknown(DateTimeOffset now) => new(false, false, false, now, IsUnknown: true);
}

/// <summary>Host memory in MiB.</summary>
/// <param name="TotalMb">Total physical memory.</param>
/// <param name="FreeMb">Memory the host can still hand out.</param>
public sealed record HostMemory(long TotalMb, long FreeMb);

/// <summary>A running VM as the host sees it.</summary>
/// <param name="Vm">The VM.</param>
/// <param name="Activity">Its current activity.</param>
/// <param name="CanSaveState">False when the backend cannot save it (for example Windows Sandbox).</param>
public sealed record RunningVm(DensityVm Vm, VmActivity Activity, bool CanSaveState);

/// <summary>One governor decision, kept in the recent-events ring for the status panel.</summary>
/// <param name="At">When it happened.</param>
/// <param name="Kind">One of <see cref="DensityEventKinds"/>.</param>
/// <param name="VmId">The VM concerned.</param>
/// <param name="Detail">Short human-readable detail.</param>
public sealed record DensityEvent(DateTimeOffset At, string Kind, Guid VmId, string Detail);

/// <summary>The event kinds the governor records.</summary>
public static class DensityEventKinds
{
    /// <summary>A VM was admitted.</summary>
    public const string Admitted = "admitted";

    /// <summary>An idle VM was saved to make room.</summary>
    public const string SavedForRoom = "saved-for-room";

    /// <summary>A request could not start at once and waits in the queue.</summary>
    public const string Queued = "queued";

    /// <summary>A queued request left the queue because it was admitted.</summary>
    public const string Dequeued = "dequeued";

    /// <summary>A queued request waited past the queue timeout.</summary>
    public const string TimedOut = "timed-out";

    /// <summary>Saving a victim failed; it is skipped for a while and the request replans.</summary>
    public const string SaveFailed = "save-failed";

    /// <summary>The host probe or VM directory failed, so the request was admitted without control (density is an optimisation).</summary>
    public const string ProbeFailed = "probe-failed";
}

/// <summary>A point-in-time view of the governor for the status panel.</summary>
/// <param name="HostFreeMb">Host free memory at the last read.</param>
/// <param name="ReservedMb">Memory reserved by admissions whose VMs have not yet been reported started.</param>
/// <param name="Running">Running VMs at the last read.</param>
/// <param name="Queued">Requests waiting for room (including ones whose victims are being saved).</param>
/// <param name="RecentEvents">The most recent events, oldest first.</param>
public sealed record DensitySnapshot(long HostFreeMb, long ReservedMb, int Running, int Queued, IReadOnlyList<DensityEvent> RecentEvents);
