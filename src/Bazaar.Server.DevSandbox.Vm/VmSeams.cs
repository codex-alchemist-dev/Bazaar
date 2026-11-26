namespace Bazaar.Server.DevSandbox.Vm;

/// <summary>The guest operating system a sandbox VM runs.</summary>
public enum SandboxVmOs
{
    Windows,
    Linux,
}

/// <summary>How the engine runs one module: image, bundle and entry command. Comes from <see cref="ISandboxVmModuleSource"/>, never from the guest.</summary>
/// <param name="Os">The guest OS the module needs.</param>
/// <param name="Image">The golden image name (doc 16) the sandbox profile is a preset over.</param>
/// <param name="BundleRef">Host-side reference to the read-only module bundle.</param>
/// <param name="EntryCommand">The command run inside the guest.</param>
public sealed record SandboxVmModulePlan(SandboxVmOs Os, string Image, string BundleRef, string EntryCommand);

/// <summary>What the host needs to create one disposable sandbox-profile VM.</summary>
/// <param name="RunId">The run, for naming and audit only.</param>
/// <param name="Os">The guest OS.</param>
/// <param name="Image">The golden image name.</param>
/// <param name="MemoryLimitMb">Memory ceiling from the spec.</param>
public sealed record SandboxVmRequest(string RunId, SandboxVmOs Os, string Image, int MemoryLimitMb);

/// <summary>Opaque handle to one created VM. Never reused between runs.</summary>
/// <param name="VmId">A VM id that is unique for the life of the host.</param>
public sealed record SandboxVmHandle(string VmId);

/// <summary>Where the module's image, bundle and entry command come from (by id and version).</summary>
public interface ISandboxVmModuleSource
{
    /// <summary>Returns the plan, or null when the module is unknown.</summary>
    /// <param name="moduleId">The module id.</param>
    /// <param name="moduleVersion">The module version.</param>
    /// <param name="ct">Cancellation.</param>
    ValueTask<SandboxVmModulePlan?> GetPlanAsync(string moduleId, string moduleVersion, CancellationToken ct);
}

/// <summary>
/// Creates and destroys the disposable sandbox VM (doc 48 section 2.1, VM engine). To be adapted onto the Vms module later.
/// Every method must be idempotent: calling Stop or Retire twice, or on a VM that never started, is not an error.
/// </summary>
public interface ISandboxVmHost
{
    /// <summary>Creates a fresh VM from the image. It must not be started and must have no network and no mounts.</summary>
    /// <param name="request">What to create.</param>
    /// <param name="ct">Cancellation.</param>
    Task<SandboxVmHandle> CreateAsync(SandboxVmRequest request, CancellationToken ct);

    /// <summary>Starts the VM.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="ct">Cancellation.</param>
    Task StartAsync(SandboxVmHandle vm, CancellationToken ct);

    /// <summary>Stops the VM. Idempotent.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="ct">Cancellation.</param>
    Task StopAsync(SandboxVmHandle vm, CancellationToken ct);

    /// <summary>Destroys the VM and its disks. Idempotent. After this the handle is dead.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="ct">Cancellation.</param>
    Task RetireAsync(SandboxVmHandle vm, CancellationToken ct);
}

/// <summary>
/// The only mounts a sandbox VM may have: one read-only module bundle and one read-write scratch. There is no method for a Soul,
/// workspace or any other host path, by design.
/// </summary>
public interface ISandboxVmMounts
{
    /// <summary>Attaches the module bundle read-only.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="bundleRef">Host-side bundle reference.</param>
    /// <param name="ct">Cancellation.</param>
    Task AttachModuleBundleAsync(SandboxVmHandle vm, string bundleRef, CancellationToken ct);

    /// <summary>Attaches one read-write scratch mount holding the test company records (name to text) and nothing else.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="companyRecords">The synthetic company records.</param>
    /// <param name="ct">Cancellation.</param>
    Task AttachScratchAsync(SandboxVmHandle vm, IReadOnlyDictionary<string, string> companyRecords, CancellationToken ct);

    /// <summary>Detaches every mount of the VM. Idempotent.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="ct">Cancellation.</param>
    Task DetachAllAsync(SandboxVmHandle vm, CancellationToken ct);
}

/// <summary>Egress control for the VM (to be adapted onto <c>Bazaar.Server.VmEgress</c> and <c>EgressRules</c>).</summary>
public interface ISandboxVmEgress
{
    /// <summary>Allows exactly these test hosts and nothing else. Never called with an empty list.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="allowedTestHosts">The test hosts.</param>
    /// <param name="ct">Cancellation.</param>
    Task ApplyAsync(SandboxVmHandle vm, IReadOnlyList<string> allowedTestHosts, CancellationToken ct);

    /// <summary>Blocks all egress and confirms it. Idempotent; used for an empty allowlist, on violation and on teardown.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="ct">Cancellation.</param>
    Task BlockAllAsync(SandboxVmHandle vm, CancellationToken ct);
}

/// <summary>Runs the entry command in the guest and streams what the guest reports. Reports are data, never trusted for policy.</summary>
public interface ISandboxVmGuestChannel
{
    /// <summary>Runs the command and yields each guest-reported action as it happens.</summary>
    /// <param name="vm">The VM.</param>
    /// <param name="plan">The module plan.</param>
    /// <param name="ct">Cancellation.</param>
    IAsyncEnumerable<SandboxAction> RunAsync(SandboxVmHandle vm, SandboxVmModulePlan plan, CancellationToken ct);
}
