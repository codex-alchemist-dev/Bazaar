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
