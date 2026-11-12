namespace Bazaar.Components.ManagedPostgres;

/// <summary>Runs PostgreSQL tools and child processes. The real implementation uses System.Diagnostics.Process.</summary>
public interface IPostgresCommandRunner
{
    /// <summary>
    /// Runs a tool to completion and returns its exit code and combined output. <paramref name="environment"/> adds
    /// variables for this run only (for example PGPASSWORD); secrets go there, never into <paramref name="arguments"/>.
    /// <paramref name="workingDirectory"/> is where the tool writes its logs and sockets (pg_upgrade needs it writable).
    /// <paramref name="standardInput"/> is written to the tool's stdin and then closed; secrets such as SQL that sets a
    /// password go there, never into <paramref name="arguments"/> or a file.
    /// </summary>
    Task<PostgresCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null,
        string? workingDirectory = null,
        string? standardInput = null);

    /// <summary>Starts a long-running child (postgres) that this runner owns.</summary>
    IPostgresChild Start(string fileName, IReadOnlyList<string> arguments);

    /// <summary>Wraps a postgres process that was started earlier and is still alive, so it can be watched and stopped.</summary>
    IPostgresChild Attach(int processId);

    /// <summary>True when a process with this id exists and has not exited.</summary>
    bool IsProcessAlive(int processId);
}

/// <summary>Exit code and output of a PostgreSQL tool run.</summary>
public sealed record PostgresCommandResult(int ExitCode, string Output);

/// <summary>A postgres process the controller is watching or has started.</summary>
public interface IPostgresChild : IDisposable
{
    /// <summary>Operating-system process id.</summary>
    int ProcessId { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Waits for exit and returns the exit code, or -1 when the code is unknown (an attached process).</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Terminates the process immediately. Does nothing when it has already exited.</summary>
    void Kill();
}
