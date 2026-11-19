namespace Bazaar.Server.Vms.MicroVm;

/// <summary>Settings of the microVM provider (doc 62 section 7). Paths are explicit and absolute; nothing is read from a profile default.</summary>
public sealed record MicroVmOptions
{
    public MicroVmMonitorKind Monitor { get; init; } = MicroVmMonitorKind.Firecracker;

    /// <summary>Golden image version id that stands for the sealed kernel and root file system (parent content pinned).</summary>
    public required Guid ImageVersion { get; init; }

    /// <summary>Directory that holds per-VM copy-on-write disks and monitor sockets.</summary>
    public required string StateDirectory { get; init; }

    /// <summary><c>vms.microvm.max_concurrent</c>.</summary>
    public int MaxConcurrent { get; init; } = 8;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(StateDirectory) || !Path.IsPathRooted(StateDirectory))
        {
            throw new InvalidOperationException("vms.microvm state directory must be an explicit absolute path.");
        }

        if (!Enum.IsDefined(Monitor))
        {
            throw new InvalidOperationException("vms.microvm.monitor is not a known monitor.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConcurrent, 1);
    }
}
