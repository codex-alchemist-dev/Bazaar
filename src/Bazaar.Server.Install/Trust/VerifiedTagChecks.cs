namespace Bazaar.Server.Install.Trust;

/// <summary>
/// The state of one check for one module version. A check that has not run yet is <see cref="Pending"/>, which is not the
/// same as <see cref="Failed"/>: a pending check holds the tag back but does not revoke it (doc 48 section 3).
/// </summary>
public enum CheckState
{
    Passed,
    Failed,
    Pending,
}

/// <summary>
/// The recorded check results for one module version (doc 48 section 3).
/// </summary>
/// <param name="Version">The module version the results belong to. Must be a SemVer version.</param>
/// <param name="CheckedAt">When the checks were recorded.</param>
/// <param name="Licence">Licence check: SPDX detection and AGPL compatibility, including dependencies.</param>
/// <param name="Malware">Static malware scan of every file.</param>
/// <param name="Vulnerability">SBOM against the advisory feed.</param>
/// <param name="Signature">Publisher key valid, unrevoked and in its validity dates.</param>
/// <param name="HumanReview">A human reviewer recorded a review for this version. An agent review never counts.</param>
/// <param name="MonitoredSandboxRun">The monitored sandbox run showed no undeclared access.</param>
public sealed record VerifiedTagResults(
    string Version,
    DateTimeOffset CheckedAt,
    CheckState Licence,
    CheckState Malware,
    CheckState Vulnerability,
    CheckState Signature,
    CheckState HumanReview,
    CheckState MonitoredSandboxRun);

/// <summary>The Verified tag decision for one module version.</summary>
/// <param name="Version">The module version.</param>
/// <param name="Verified">True when every check passed, and no version up to and including this one has failed a check.</param>
/// <param name="FailedChecks">The names of this version's checks that failed, in a fixed order.</param>
/// <param name="PendingChecks">The names of this version's checks that have not run yet, in the same fixed order.</param>
/// <param name="RevokedBy">The first version in the line that failed a check, when the tag is revoked from here on. Null when not revoked.</param>
public sealed record VerifiedTagDecision(
    string Version,
    bool Verified,
    IReadOnlyList<string> FailedChecks,
    IReadOnlyList<string> PendingChecks,
    string? RevokedBy);

/// <summary>
/// The Verified tag as a pure function over recorded check results (doc 48 section 3). The tag needs every automated and
/// human check plus a monitored sandbox run. A version whose check failed revokes the tag from that version onward in its
/// module line. A pending check only withholds the tag for that version. The tag never bypasses permission approval; it is
/// information for the owner's decision.
/// </summary>
public static class VerifiedTagChecks
{
    /// <summary>
    /// Evaluates one module line. The versions are checked against SemVer precedence: the list must be strictly ascending,
    /// and a duplicate or out-of-order version is refused. Each version is judged on its own checks, and the first version
    /// that failed a check revokes the tag from that version onward.
    /// </summary>
    public static IReadOnlyList<VerifiedTagDecision> Evaluate(IReadOnlyList<VerifiedTagResults> lineOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(lineOldestFirst);
        RequireAscending(lineOldestFirst);

        var decisions = new List<VerifiedTagDecision>(lineOldestFirst.Count);
        string? revokedBy = null;
        foreach (var results in lineOldestFirst)
        {
            var failed = ChecksIn(results, CheckState.Failed);
            var pending = ChecksIn(results, CheckState.Pending);
            if (failed.Count > 0)
            {
                revokedBy ??= results.Version;
            }

            var verified = failed.Count == 0 && pending.Count == 0 && revokedBy is null;
            decisions.Add(new VerifiedTagDecision(results.Version, verified, failed, pending, revokedBy));
        }

        return decisions;
    }

    /// <summary>The names of the checks that failed, in the fixed order of the doc.</summary>
    public static IReadOnlyList<string> FailedChecks(VerifiedTagResults results) => ChecksIn(results, CheckState.Failed);

    /// <summary>The names of the checks that have not run yet, in the fixed order of the doc.</summary>
    public static IReadOnlyList<string> PendingChecks(VerifiedTagResults results) => ChecksIn(results, CheckState.Pending);

    private static void RequireAscending(IReadOnlyList<VerifiedTagResults> line)
    {
        for (var i = 0; i < line.Count; i++)
        {
            ArgumentNullException.ThrowIfNull(line[i]);
            if (i == 0)
            {
                ModuleVersionOrder.Parse(line[0].Version);
                continue;
            }

            var order = ModuleVersionOrder.Compare(line[i - 1].Version, line[i].Version);
            if (order == 0)
            {
                throw new ArgumentException($"Version {line[i].Version} appears more than once in the line.", nameof(line));
            }

            if (order > 0)
            {
                throw new ArgumentException(
                    $"Versions must be oldest first: {line[i].Version} comes after {line[i - 1].Version} in the list but is older.",
                    nameof(line));
            }
        }
    }

    // The fixed order of the doc: licence, malware, vulnerability, signature, human review, sandbox run.
    private static List<string> ChecksIn(VerifiedTagResults results, CheckState wanted)
    {
        ArgumentNullException.ThrowIfNull(results);

        var named = new (string Name, CheckState State)[]
        {
            ("licence", results.Licence),
            ("malware", results.Malware),
            ("vulnerability", results.Vulnerability),
            ("signature", results.Signature),
            ("human-review", results.HumanReview),
            ("sandbox-run", results.MonitoredSandboxRun),
        };

        return named.Where(check => check.State == wanted).Select(check => check.Name).ToList();
    }
}
