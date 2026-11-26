using Bazaar.Contracts.Modules;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>
/// The only seam to Windows Sandbox. Every call that touches the host goes through here, so the backend is tested with a fake
/// and cloud CI never starts a sandbox. The real adapter is written after spike S-WSB runs on the owner's PC (doc 50 section 8).
/// </summary>
public interface IWindowsSandboxHost
{
    /// <summary>Writes a <c>.wsb</c> config for one instance and returns its path.</summary>
    Task<string> WriteConfigAsync(string instanceName, string configXml, CancellationToken ct);

    /// <summary>Starts a sandbox from a config and returns the host's instance id.</summary>
    Task<string> LaunchAsync(string configPath, CancellationToken ct);

    /// <summary>Stops an instance. A forced stop kills it without a guest shutdown. Either way its writable state is discarded.</summary>
    Task ShutdownAsync(string instanceId, bool force, CancellationToken ct);

    /// <summary>Reports whether an instance is still running.</summary>
    Task<bool> IsRunningAsync(string instanceId, CancellationToken ct);

    /// <summary>Counts every running sandbox on the host, including ones this server did not start.</summary>
    Task<int> CountRunningAsync(CancellationToken ct);

    /// <summary>Deletes a config file that is no longer needed.</summary>
    Task DeleteConfigAsync(string configPath, CancellationToken ct);

    /// <summary>
    /// Runs one command inside a running instance as <c>System</c> (doc 50 section 9). The command is a program path plus arguments,
    /// never a shell string. The result carries the process exit code and captured output; a non-zero exit is data, not an exception.
    /// </summary>
    Task<SandboxExecResult> ExecAsync(string instanceId, IReadOnlyList<string> command, CancellationToken ct);
}

/// <summary>The exit code and captured output of one command run inside a sandbox.</summary>
public sealed record SandboxExecResult(int ExitCode, string StandardOutput, string StandardError);
