namespace Bazaar.Server.Modules.LineMove;

/// <summary>An installed extension and whether it has an artifact for the target line.</summary>
public sealed record InstalledExtension(string Name, bool HasTargetLineArtifact);

/// <summary>The facts the preflight rules read. The caller measures them; this record holds no I/O.</summary>
/// <param name="Extensions">Every installed extension (pgvector, and pg_search on Linux).</param>
/// <param name="FreeDiskBytes">Free disk space on the data volume.</param>
/// <param name="SnapshotBytes">Expected size of the snapshot.</param>
/// <param name="UpgradeBytes">Expected extra space the upgrade needs.</param>
/// <param name="KodoGreenOnCurrentLine">True when Kodo reports green on the current line.</param>
/// <param name="RestoreDrillAge">Age of the last restore drill, or null when none has run.</param>
/// <param name="MaxRestoreDrillAge">Configured maximum drill age. The drill must be younger than this.</param>
public sealed record LineMovePreflightInputs(
    IReadOnlyList<InstalledExtension> Extensions,
    long FreeDiskBytes,
    long SnapshotBytes,
    long UpgradeBytes,
    bool KodoGreenOnCurrentLine,
    TimeSpan? RestoreDrillAge,
    TimeSpan MaxRestoreDrillAge);

/// <summary>The preflight rules. Each rule is listed separately so a failure names the rule that failed.</summary>
public enum PreflightRule
{
    /// <summary>Every installed extension has a line-target artifact.</summary>
    ExtensionArtifacts,

    /// <summary>Free disk covers the snapshot plus the upgrade.</summary>
    DiskSpace,

    /// <summary>Kodo is green on the current line.</summary>
    KodoGreen,

    /// <summary>The last restore drill is younger than the configured maximum.</summary>
    RestoreDrillAge,
}

/// <summary>One rule's result.</summary>
public sealed record PreflightCheck(PreflightRule Rule, bool Passed, string Detail);

/// <summary>The preflight result: every rule, passed or not.</summary>
public sealed record PreflightResult(IReadOnlyList<PreflightCheck> Checks)
{
    /// <summary>True when every rule passed.</summary>
    public bool Passed => Checks.All(check => check.Passed);
}

/// <summary>
/// Preflight for a major line move as a pure function (doc 42 section 10.3, step 1). Every rule is evaluated,
/// so the owner sees all failures at once.
/// </summary>
public static class LineMovePreflight
{
    /// <summary>Evaluates the four preflight rules against <paramref name="inputs"/>.</summary>
    public static PreflightResult Evaluate(LineMovePreflightInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(inputs.Extensions);
        if (inputs.FreeDiskBytes < 0 || inputs.SnapshotBytes < 0 || inputs.UpgradeBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(inputs), "Byte counts cannot be negative.");
        if (inputs.MaxRestoreDrillAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(inputs), "The maximum drill age cannot be negative.");

        return new PreflightResult(
        [
            ExtensionArtifacts(inputs.Extensions),
            DiskSpace(inputs),
            KodoGreen(inputs.KodoGreenOnCurrentLine),
            RestoreDrillAge(inputs.RestoreDrillAge, inputs.MaxRestoreDrillAge),
        ]);
    }

    private static PreflightCheck ExtensionArtifacts(IReadOnlyList<InstalledExtension> extensions)
    {
        var missing = extensions.Where(extension => !extension.HasTargetLineArtifact).Select(extension => extension.Name).ToList();
        return missing.Count == 0
            ? new PreflightCheck(PreflightRule.ExtensionArtifacts, true, $"{extensions.Count} extension(s) have a target-line artifact.")
            : new PreflightCheck(PreflightRule.ExtensionArtifacts, false, $"No target-line artifact for: {string.Join(", ", missing)}.");
    }

    private static PreflightCheck DiskSpace(LineMovePreflightInputs inputs)
    {
        // Compared as "free minus snapshot is at least upgrade" so the sum can never overflow.
        var passed = inputs.FreeDiskBytes - inputs.SnapshotBytes >= inputs.UpgradeBytes;
        var detail = $"Free {inputs.FreeDiskBytes} bytes; needs snapshot {inputs.SnapshotBytes} plus upgrade {inputs.UpgradeBytes}.";
        return new PreflightCheck(PreflightRule.DiskSpace, passed, detail);
    }

    private static PreflightCheck KodoGreen(bool green) =>
        green
            ? new PreflightCheck(PreflightRule.KodoGreen, true, "Kodo is green on the current line.")
            : new PreflightCheck(PreflightRule.KodoGreen, false, "Kodo is not green on the current line.");

    private static PreflightCheck RestoreDrillAge(TimeSpan? age, TimeSpan maximum)
    {
        if (age is null)
            return new PreflightCheck(PreflightRule.RestoreDrillAge, false, "No restore drill has run.");

        var passed = age.Value < maximum;
        var detail = $"Last restore drill was {age.Value} ago; the maximum is {maximum}.";
        return new PreflightCheck(PreflightRule.RestoreDrillAge, passed, detail);
    }
}
