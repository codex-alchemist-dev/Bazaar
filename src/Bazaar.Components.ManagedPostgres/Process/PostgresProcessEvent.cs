namespace Bazaar.Components.ManagedPostgres;

/// <summary>What the controller reports through <see cref="PostgresProcessController.ProcessEvent"/>.</summary>
public enum PostgresProcessEventKind
{
    /// <summary>A new postgres child was started and is accepting connections.</summary>
    Started,

    /// <summary>An already running cluster with a matching pid and port was adopted.</summary>
    Adopted,

    /// <summary>The supervised child exited while the controller was not stopping it.</summary>
    Exited,

    /// <summary>A restart was scheduled after the backoff delay.</summary>
    RestartScheduled,

    /// <summary>The restarted child is accepting connections.</summary>
    Restarted,

    /// <summary>A restart attempt failed (the child did not start or accept connections).</summary>
    RestartAttemptFailed,

    /// <summary>The restart limit was reached. The controller is now Failed and does not restart again.</summary>
    RestartsExhausted,

    /// <summary>A pg_ctl shutdown request returned an error. Stop continues with the next step.</summary>
    ShutdownRequestFailed,

    /// <summary>The fast shutdown did not finish in time, so an immediate shutdown was requested.</summary>
    ForcedShutdown,

    /// <summary>The child was stopped on request.</summary>
    Stopped,
}

/// <summary>One report from the controller. Handlers must not throw.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Message">Human-readable detail. Never contains the superuser password.</param>
/// <param name="ExitCode">Exit code of the child when known.</param>
/// <param name="Attempt">Consecutive restart attempt number for restart events.</param>
/// <param name="RestartDelay">Backoff delay for <see cref="PostgresProcessEventKind.RestartScheduled"/>.</param>
public sealed record PostgresProcessEvent(
    PostgresProcessEventKind Kind,
    string Message,
    int? ExitCode = null,
    int Attempt = 0,
    TimeSpan? RestartDelay = null);

/// <summary>Lifecycle state of the managed cluster as the controller sees it.</summary>
public enum PostgresProcessState
{
    /// <summary>No child is running.</summary>
    Stopped,

    /// <summary>A child is running and accepting connections.</summary>
    Running,

    /// <summary>The child exited unexpectedly and a restart is pending or in progress.</summary>
    Restarting,

    /// <summary>The restart limit was reached.</summary>
    Failed,
}
