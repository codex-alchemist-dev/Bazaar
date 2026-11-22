namespace Bazaar.Server.Coding.Checks;

/// <summary>Turns the pure check results into one pass or fail.</summary>
public static class CheckSummary
{
    /// <summary>
    /// Passes only when the build exited 0, every flag code is in <paramref name="allowedCodes"/>, and there is no count finding.
    /// A null exit code means the build did not run, which is not a pass.
    /// </summary>
    public static bool Summarize(int? buildExitCode, IReadOnlyList<FlagFinding> flags, FlagFinding? countFinding, IReadOnlySet<string> allowedCodes)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(allowedCodes);
        if (buildExitCode != 0 || countFinding is not null)
        {
            return false;
        }

        return flags.All(flag => allowedCodes.Contains(flag.Code));
    }
}
