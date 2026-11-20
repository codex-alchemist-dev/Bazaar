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

    /// <summary>Absolute path of the Firecracker or Cloud Hypervisor binary. Required by the real monitor adapters.</summary>
    public string? MonitorBinaryPath { get; init; }

    /// <summary>Absolute path of the Firecracker jailer. When set, Firecracker runs under it (Cloud Hypervisor ignores it).</summary>
    public string? JailerPath { get; init; }

    /// <summary>Unprivileged user id the jailer drops to. Required with <see cref="JailerPath"/>.</summary>
    public int JailerUid { get; init; }

    /// <summary>Unprivileged group id the jailer drops to. Required with <see cref="JailerPath"/>.</summary>
    public int JailerGid { get; init; }

    /// <summary>Absolute path of the sealed guest kernel image.</summary>
    public string? KernelPath { get; init; }

    /// <summary>Absolute path of the sealed parent root file system.</summary>
    public string? RootfsPath { get; init; }

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
        RequireAbsoluteIfSet(MonitorBinaryPath, "monitor binary path");
        RequireAbsoluteIfSet(JailerPath, "jailer path");
        RequireAbsoluteIfSet(KernelPath, "kernel path");
        RequireAbsoluteIfSet(RootfsPath, "rootfs path");
        if (JailerPath is not null && (JailerUid <= 0 || JailerGid <= 0))
        {
            throw new InvalidOperationException("vms.microvm jailer needs an unprivileged uid and gid (not root).");
        }
    }

    private static void RequireAbsoluteIfSet(string? path, string what)
    {
        if (path is not null && (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)))
        {
            throw new InvalidOperationException($"vms.microvm {what} must be an explicit absolute path.");
        }
    }
}
