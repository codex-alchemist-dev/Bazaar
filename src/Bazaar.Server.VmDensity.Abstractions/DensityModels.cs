namespace Bazaar.Server.VmDensity.Abstractions;

/// <summary>
/// The guest operating system of a VM. The governor saves idle Windows VMs before idle Linux VMs (doc 59 section 8), and idle Android
/// guests last (doc 62 section 9.3). A microVM guest is a Linux guest.
/// </summary>
public enum DensityGuestOs
{
    /// <summary>A Windows guest.</summary>
    Windows,

    /// <summary>A Linux guest.</summary>
    Linux,

    /// <summary>An Android emulator guest (doc 62). Saved only when its backend can save state, and after Windows and Linux guests.</summary>
    Android,

    /// <summary>An iOS Simulator guest on a Mac node (doc 62). Ranked with the other device guests.</summary>
    Ios,

    /// <summary>A macOS virtual machine on a Mac node (doc 62). Ranked with the other device guests.</summary>
    MacOs,
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
public sealed record VmActivity(bool HasRunningExec, bool HasOpenTerminal, bool HasBrowserSession, DateTimeOffset LastUsedAt)
{
    /// <summary>True when an exec, a terminal or a browser session is active.</summary>
    public bool IsBusy => HasRunningExec || HasOpenTerminal || HasBrowserSession;
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
