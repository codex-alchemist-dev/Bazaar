namespace Bazaar.Server.Vms.MicroVm.Monitors;

/// <summary>Sends one HTTP request to a monitor over its per-VM Unix domain socket.</summary>
public interface IMicroVmApiClient
{
    Task<MicroVmApiResponse> SendAsync(string socketPath, MicroVmApiRequest request, CancellationToken ct);
}

/// <summary>A launched monitor process.</summary>
public interface IMicroVmProcess : IDisposable
{
    bool HasExited { get; }

    int ExitCode { get; }

    Task WaitForExitAsync(CancellationToken ct);

    void Kill();
}

/// <summary>Starts a process from an argument list. Never a shell string.</summary>
public interface IMicroVmProcessLauncher
{
    IMicroVmProcess Launch(IReadOnlyList<string> argv);
}

/// <summary>Creates and removes the per-VM tap device.</summary>
public interface IMicroVmTapNetwork
{
    Task CreateAsync(string tapName, CancellationToken ct);

    Task DeleteAsync(string tapName, CancellationToken ct);
}

/// <summary>Host file system seam for copy-on-write disks. A copy is a reflink only where this says the directory supports it.</summary>
public interface IMicroVmDiskCloner
{
    bool ReflinkSupported(string directory);

    Task ReflinkAsync(string source, string destination, CancellationToken ct);
}

/// <summary>Cloner for hosts without a reflink implementation: always reports unsupported, so a sparse copy is used.</summary>
public sealed class NoReflinkDiskCloner : IMicroVmDiskCloner
{
    public bool ReflinkSupported(string directory) => false;

    public Task ReflinkAsync(string source, string destination, CancellationToken ct) =>
        throw new NotSupportedException("Reflink copy is not implemented on this host.");
}
