namespace Bazaar.Server.Valkey;

/// <summary>
/// Everything the launcher needs to start a managed <c>valkey-server</c>. The configuration (which holds the generated password) is
/// handed to the child on its standard input and is never placed on the command line or on disk.
/// </summary>
public sealed class ValkeyLaunchSpec
{
    /// <summary>Creates the spec.</summary>
    /// <param name="executable">The server binary, from <see cref="ValkeyCacheOptions.BinaryPath"/>.</param>
    /// <param name="configText">The server configuration, read by the child from stdin (<c>-</c>).</param>
    /// <param name="parentDeathWrapper">
    /// A wrapper that sets the parent-death signal before it runs the server (<see cref="ValkeyCacheOptions.ParentDeathWrapper"/>),
    /// or empty for none. The launcher fails closed when the wrapper cannot run.
    /// </param>
    public ValkeyLaunchSpec(string executable, string configText, string parentDeathWrapper)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrEmpty(configText);
        ArgumentNullException.ThrowIfNull(parentDeathWrapper);
        Executable = executable;
        ConfigText = configText;
        ParentDeathWrapper = parentDeathWrapper;
    }

    /// <summary>The server binary.</summary>
    public string Executable { get; }

    /// <summary>The server configuration, including the password. Never log it.</summary>
    public string ConfigText { get; }

    /// <summary>The parent-death wrapper, or empty.</summary>
    public string ParentDeathWrapper { get; }

    /// <summary>The server arguments: one dash, meaning "read the configuration from standard input".</summary>
    public IReadOnlyList<string> Arguments { get; } = ["-"];

    /// <summary>Never includes the configuration.</summary>
    public override string ToString() => $"valkey launch of {Executable} (configuration redacted)";
}

/// <summary>Starts a managed child process. The real implementation runs a process; tests inject a fake.</summary>
public interface IValkeyProcessLauncher
{
    /// <summary>Starts the child and writes its configuration to stdin. Throws when it cannot start; the supervisor counts that as a failed attempt.</summary>
    Task<IValkeyChild> StartAsync(ValkeyLaunchSpec spec, CancellationToken ct);
}

/// <summary>A running managed child.</summary>
public interface IValkeyChild : IAsyncDisposable
{
    /// <summary>The operating system process id, written to the pid file so a later host can sweep a stale child.</summary>
    int ProcessId { get; }

    /// <summary>Completes when the child exits, for any reason.</summary>
    Task Exited { get; }

    /// <summary>Stops the child if it is still running. Never throws.</summary>
    void Kill();
}
