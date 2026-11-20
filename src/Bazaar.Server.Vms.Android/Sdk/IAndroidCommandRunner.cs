namespace Bazaar.Server.Vms.Android.Sdk;

/// <summary>One external command: an executable, its argument list, the exact environment and limits. Never run through a shell.</summary>
public sealed record AndroidCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    TimeSpan Timeout,
    byte[]? Stdin = null);

/// <summary>Result of a finished command. Standard output stays binary because screencap returns PNG bytes.</summary>
public sealed record AndroidCommandResult(int ExitCode, byte[] Stdout, string Stderr);

/// <summary>A long-running child (the emulator) that the host tracks by handle.</summary>
public interface IAndroidProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>Kills the process and its children.</summary>
    void Kill();

    Task WaitForExitAsync(CancellationToken ct);
}

/// <summary>
/// The only place that starts Android SDK executables. Implementations never use a shell, never inherit the parent environment
/// (the child sees exactly <see cref="AndroidCommand.Environment"/>), and enforce the timeout and output cap.
/// </summary>
public interface IAndroidCommandRunner
{
    /// <summary>Runs to completion. Throws <see cref="TimeoutException"/> after killing the process tree when the timeout elapses.</summary>
    Task<AndroidCommandResult> RunAsync(AndroidCommand command, CancellationToken ct);

    /// <summary>Starts a process that outlives the call and returns its handle.</summary>
    IAndroidProcess StartDetached(AndroidCommand command);
}
