using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.MicroVm;

/// <summary>
/// The only seam to a microVM monitor (Firecracker or Cloud Hypervisor, driven over a per-VM Unix socket). Cloud CI uses a fake;
/// the real adapters are separate work packages and a gated integration category (doc 62 WP11a, spike S-MVM).
/// </summary>
public interface IMicroVmMonitor
{
    /// <summary>Makes a copy-on-write disk for one VM from the sealed parent image.</summary>
    Task CreateDiskAsync(string name, int vcpu, int memoryMb, int diskCapGb, CancellationToken ct);

    Task BootAsync(string name, CancellationToken ct);

    Task StopAsync(string name, bool force, CancellationToken ct);

    Task<bool> IsRunningAsync(string name, CancellationToken ct);

    /// <summary>Deletes the disk, socket and tap of a retired VM.</summary>
    Task DeleteAsync(string name, CancellationToken ct);
}

/// <summary>Probes the host. Synchronous and read-only.</summary>
public interface IMicroVmHostFacts
{
    HostFacts Current();
}
