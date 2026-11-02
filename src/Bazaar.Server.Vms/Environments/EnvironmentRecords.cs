namespace Bazaar.Server.Vms.Environments;

/// <summary>Guest operating system of a golden image or VM.</summary>
public enum VmOs
{
    Windows,
    Linux,
}

/// <summary>Lifecycle of a golden image (<c>golden_image.state</c>).</summary>
public enum GoldenImageState
{
    Building,
    Ready,
    Retired,
}

/// <summary>Lifecycle of an environment VM (<c>environment_vm.state</c>).</summary>
public enum VmState
{
    Absent,
    Provisioning,
    Stopped,
    Running,
    Saved,
    Checkpointed,
    Restored,
    Retired,
}

/// <summary>What a scoped mount exposes to the guest (<c>scoped_mount.kind</c>).</summary>
public enum MountKind
{
    Soul,
    Workspace,
}

/// <summary>Access mode of a scoped mount (<c>scoped_mount.mode</c>).</summary>
public enum MountMode
{
    Ro,
    Rw,
}

/// <summary>A versioned base image for one OS (<c>golden_image</c>).</summary>
public sealed record GoldenImageRecord(
    Guid Id,
    VmOs Os,
    int Version,
    string Sha256,
    string GuestAgentVersion,
    GoldenImageState State,
    DateTimeOffset CreatedAt);

/// <summary>An agent's VM for one OS (<c>environment_vm</c>).</summary>
public sealed record EnvironmentVmRecord(
    Guid Id,
    Guid AgentId,
    VmOs Os,
    Guid ParentImageId,
    string? BackendRef,
    VmState State,
    int Vcpu,
    int MemoryMb,
    int DiskCapGb,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A named or unnamed checkpoint of a VM (<c>environment_checkpoint</c>).</summary>
public sealed record EnvironmentCheckpointRecord(
    Guid Id,
    Guid VmId,
    string BackendRef,
    string? Label,
    DateTimeOffset CreatedAt);

/// <summary>A host path exposed to a VM (<c>scoped_mount</c>). A null <see cref="DetachedAt"/> means attached.</summary>
public sealed record ScopedMountRecord(
    Guid Id,
    Guid AgentId,
    Guid VmId,
    MountKind Kind,
    string HostPath,
    string GuestPath,
    MountMode Mode,
    DateTimeOffset? DetachedAt);

/// <summary>Input for a new exec log row. The row id is assigned by the database.</summary>
public sealed record NewExecLog(
    Guid VmId,
    DateTimeOffset StartedAt,
    string ArgvHash,
    int TimeoutSeconds);

/// <summary>An exec log row (<c>exec_log</c>). Exit code and output size stay null until the exec completes.</summary>
public sealed record ExecLogRecord(
    long Id,
    Guid VmId,
    DateTimeOffset StartedAt,
    string ArgvHash,
    int TimeoutSeconds,
    int? ExitCode,
    long? BytesOut);
