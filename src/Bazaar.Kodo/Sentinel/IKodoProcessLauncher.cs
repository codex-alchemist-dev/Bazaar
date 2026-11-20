namespace Bazaar.Kodo.Sentinel;

/// <summary>What happened to a Kodo process started for probation.</summary>
public enum KodoRunKind
{
    /// <summary>The process was still running when the window ended. The launcher stopped it.</summary>
    SurvivedWindow,

    /// <summary>The process exited inside the window.</summary>
    Exited,

    /// <summary>The process could not be started from the slot.</summary>
    FailedToStart,
}

/// <summary>The result of one probation run. <see cref="ExitCode"/> is set only when the process exited.</summary>
public sealed record KodoRunOutcome(KodoRunKind Kind, int? ExitCode);

/// <summary>
/// The process seam for the sentinel (doc 46 sections 6.3 and 7.1). The launcher starts Kodo from a verified slot in shadow
/// mode, watches it for the window, and stops it when the window ends. The OS-specific launch lives behind this interface.
/// </summary>
public interface IKodoProcessLauncher
{
    /// <summary>Runs Kodo from <paramref name="slotId"/> for <paramref name="window"/> and reports how it went.</summary>
    Task<KodoRunOutcome> RunProbationAsync(string slotId, TimeSpan window, CancellationToken cancellationToken);
}
