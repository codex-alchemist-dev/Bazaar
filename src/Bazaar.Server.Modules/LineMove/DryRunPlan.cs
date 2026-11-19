namespace Bazaar.Server.Modules.LineMove;

/// <summary>
/// One GC's expected state after the restore and the migrations (doc 33: row count and maximum sequence per GC).
/// The executor compares the restored data with these values; the runner only sequences the checks.
/// </summary>
/// <param name="GcId">GC id. Unique within one plan.</param>
/// <param name="ExpectedRowCount">Expected number of rows in the GC log.</param>
/// <param name="ExpectedMaxSequence">Expected highest sequence number in the GC log.</param>
public sealed record GcConsistencyDescriptor(string GcId, long ExpectedRowCount, long ExpectedMaxSequence);

/// <summary>
/// What a dry run restores and checks (doc 42 section 10.3, step 3). The plan names only a scratch directory, never
/// the production data directory, so a dry run cannot mutate production by construction.
/// </summary>
/// <param name="SnapshotId">Snapshot taken in the snapshot step. Must not be empty.</param>
/// <param name="SourceLine">Current major line (for example 18).</param>
/// <param name="TargetLine">Line the dry run restores onto (for example 19).</param>
/// <param name="ScratchDirectoryName">Name of the scratch data directory, relative to the scratch root. Must be a single safe name.</param>
/// <param name="MigrationNames">Migrations of doc 04, in the order they run.</param>
/// <param name="ConsistencyChecks">GC consistency checks supplied by the caller. Each GC id appears once.</param>
public sealed record DryRunPlan(
    string SnapshotId,
    int SourceLine,
    int TargetLine,
    string ScratchDirectoryName,
    IReadOnlyList<string> MigrationNames,
    IReadOnlyList<GcConsistencyDescriptor> ConsistencyChecks)
{
    /// <summary>Longest scratch directory name the plan accepts.</summary>
    public const int MaxScratchDirectoryNameLength = 64;

    /// <summary>Checks the plan's own rules. Returns every problem found; an empty list means the plan is valid.</summary>
    public IReadOnlyList<DryRunProblem> Validate()
    {
        var problems = new List<DryRunProblem>();

        if (SourceLine == TargetLine)
            problems.Add(new DryRunProblem(DryRunProblemKind.SameSourceAndTargetLine, $"Source and target are both line {SourceLine}."));

        if (string.IsNullOrWhiteSpace(SnapshotId))
            problems.Add(new DryRunProblem(DryRunProblemKind.EmptySnapshotId, "The plan names no snapshot id."));

        if (!IsSafeScratchDirectoryName(ScratchDirectoryName))
            problems.Add(new DryRunProblem(
                DryRunProblemKind.UnsafeScratchDirectoryName,
                "The scratch directory name must be 1 to 64 letters, digits, '-', '_' or '.', without leading or trailing '.' or '..'."));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in ConsistencyChecks)
        {
            if (!seen.Add(check.GcId) && reported.Add(check.GcId))
                problems.Add(new DryRunProblem(DryRunProblemKind.DuplicateGcId, $"GC '{check.GcId}' has more than one consistency check."));
        }

        return problems;
    }

    /// <summary>True when the name is one plain directory name: no separators, no traversal, no rooting, no whitespace.</summary>
    internal static bool IsSafeScratchDirectoryName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxScratchDirectoryNameLength)
            return false;
        if (name[0] == '.' || name[^1] == '.')
            return false;
        foreach (var character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
                return false;
        }

        return !name.Contains("..", StringComparison.Ordinal);
    }
}

/// <summary>The kinds of problem <see cref="DryRunPlan.Validate"/> reports.</summary>
public enum DryRunProblemKind
{
    /// <summary>The source and target lines are the same; a dry run must restore onto a different line.</summary>
    SameSourceAndTargetLine,

    /// <summary>The snapshot id is empty or whitespace.</summary>
    EmptySnapshotId,

    /// <summary>The scratch directory name is not a single safe name.</summary>
    UnsafeScratchDirectoryName,

    /// <summary>Two consistency checks name the same GC.</summary>
    DuplicateGcId,
}

/// <summary>One validation problem: its kind and a human-readable detail for the owner.</summary>
public sealed record DryRunProblem(DryRunProblemKind Kind, string Detail);
