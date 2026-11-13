namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// Inputs an executor sees for one run. <see cref="SnapshotId"/> is null until the snapshot step succeeded; the
/// runner fills it in from the snapshot step's result, so later executors always see it.
/// </summary>
public sealed record LineMoveContext(
    string MoveId,
    int FromLine,
    int ToLine,
    string ApprovingRole,
    string? SnapshotId);
