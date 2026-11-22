using Bazaar.Server.Vms.WindowsSandbox;

namespace Bazaar.Server.Vms.WindowsSandbox.Tests;

/// <summary>
/// An in-memory <see cref="IWindowsSandboxHost"/>. Records every call and keeps a set of running instances.
/// <see cref="ExternalInstances"/> adds sandboxes this server did not start, to test the host-wide limit.
/// </summary>
internal sealed class FakeWindowsSandboxHost : IWindowsSandboxHost
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _configs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private int _nextSandbox;

    public List<string> Calls { get; } = [];

    /// <summary>Running sandboxes this server did not start (counted by <see cref="CountRunningAsync"/>).</summary>
    public int ExternalInstances { get; set; }

    public IReadOnlyDictionary<string, string> Configs
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_configs, StringComparer.Ordinal);
        }
    }

    public Task<string> WriteConfigAsync(string instanceName, string configXml, CancellationToken ct)
    {
        lock (_gate)
        {
            var path = $"scratch/{instanceName}.wsb";
            _configs[path] = configXml;
            Calls.Add($"write:{instanceName}");
            return Task.FromResult(path);
        }
    }

    public Task<string> LaunchAsync(string configPath, CancellationToken ct)
    {
        lock (_gate)
        {
            var id = $"sandbox-{++_nextSandbox}";
            _running.Add(id);
            Calls.Add($"launch:{configPath}");
            return Task.FromResult(id);
        }
    }

    public Task ShutdownAsync(string instanceId, bool force, CancellationToken ct)
    {
        lock (_gate)
        {
            _running.Remove(instanceId);
            Calls.Add($"shutdown:{instanceId}:{(force ? "force" : "graceful")}");
            return Task.CompletedTask;
        }
    }

    public Task<bool> IsRunningAsync(string instanceId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_running.Contains(instanceId));
    }

    public Task<int> CountRunningAsync(CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_running.Count + ExternalInstances);
    }

    public Task DeleteConfigAsync(string configPath, CancellationToken ct)
    {
        lock (_gate)
        {
            _configs.Remove(configPath);
            Calls.Add($"delete:{configPath}");
            return Task.CompletedTask;
        }
    }

    /// <summary>Plays the guest for <see cref="ExecAsync"/>. Without it an exec returns exit code 0 at once.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<SandboxExecResult>>? ExecHandler { get; set; }

    public Task<SandboxExecResult> ExecAsync(string instanceId, IReadOnlyList<string> command, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add($"exec:{instanceId}:{string.Join(' ', command)}");
        }

        return ExecHandler?.Invoke(command, ct) ?? Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
    }

    /// <summary>Simulates a sandbox closing on its own, as a user would close the window.</summary>
    public void CloseFromOutside(string instanceId)
    {
        lock (_gate)
            _running.Remove(instanceId);
    }
}
