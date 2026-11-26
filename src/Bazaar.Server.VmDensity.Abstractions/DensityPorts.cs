namespace Bazaar.Server.VmDensity.Abstractions;

/// <summary>Reads host memory. The host adapts the platform probe (Windows performance counters, Linux <c>/proc/meminfo</c>).</summary>
public interface IHostMemoryProbe
{
    /// <summary>Reads total and free host memory.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<HostMemory> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Lists the VMs that are running now with their activity. The host adapts the lifecycle service.</summary>
public interface IDensityVmDirectory
{
    /// <summary>Lists running VMs.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<IReadOnlyList<RunningVm>> ListRunningAsync(CancellationToken cancellationToken);
}

/// <summary>Saves a VM's state. The host maps it to the lifecycle service's save (doc 16, Saved state).</summary>
public interface IDensityVmControl
{
    /// <summary>Saves the VM and returns when its memory has been released.</summary>
    /// <param name="vmId">The VM to save.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task SaveAsync(Guid vmId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads what one VM is doing right now (PROPOSED, doc 59 section 8). Whoever owns the exec channel, the terminals and the browser
/// sessions implements it; the density module registers a default that reads nothing.
/// </summary>
public interface IVmActivityReader
{
    /// <summary>
    /// Reads the activity of one running VM. Returns null when it cannot be read, and a failure counts the same: an unreadable
    /// activity is treated as busy, so the VM is never saved on a guess. An implementation must report busy when any one of the exec,
    /// terminal and browser facts is unreadable, and must not report <see cref="VmActivity.HasRunningExec"/> and the others false
    /// only because it has no source for them.
    /// </summary>
    /// <param name="vm">The VM.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<VmActivity?> ReadAsync(DensityVm vm, CancellationToken cancellationToken);
}
