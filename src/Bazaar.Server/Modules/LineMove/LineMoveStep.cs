namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// The steps of a major line move (doc 42 section 10.3), in run order. <see cref="RolledBack"/> is the rollback
/// step; it is not part of the forward order and runs only after <see cref="Switch"/> succeeded.
/// </summary>
public enum LineMoveStep
{
    /// <summary>Preflight rules (pure, see <see cref="LineMovePreflight"/>). Not an executor.</summary>
    Preflight,

    /// <summary>Base backup and file-level snapshot of the data directory; records the snapshot id.</summary>
    Snapshot,

    /// <summary>Restore the snapshot into a scratch directory on the target line; migrations and consistency checks.</summary>
    DryRun,

    /// <summary>Stop the old line, upgrade the data directory, start the new line, run migrations.</summary>
    Switch,

    /// <summary>Health contract and GC consistency checks on the new line.</summary>
    Verify,

    /// <summary>Mark the new line newest and the old line stable. Runs only after Verify passed.</summary>
    Complete,

    /// <summary>Restore the snapshot and start the old line. Downgrade is not supported.</summary>
    RolledBack,
}
