namespace Bazaar.Server.Modules.Trust;

/// <summary>
/// The recorded check results for one module version (doc 48 section 3). Each flag is true when that check passed.
/// </summary>
/// <param name="Version">The module version the results belong to.</param>
/// <param name="CheckedAt">When the checks were recorded.</param>
/// <param name="LicenceOk">Licence check: SPDX detection and AGPL compatibility, including dependencies.</param>
/// <param name="MalwareOk">Static malware scan of every file.</param>
/// <param name="VulnerabilityOk">SBOM against the advisory feed.</param>
/// <param name="SignatureOk">Publisher key valid, unrevoked and in its validity dates.</param>
/// <param name="HumanReviewRecorded">A human reviewer recorded a review for this version. An agent review never counts.</param>
/// <param name="MonitoredSandboxRunPassed">The monitored sandbox run showed no undeclared access.</param>
public sealed record VerifiedTagResults(
    string Version,
    DateTimeOffset CheckedAt,
    bool LicenceOk,
    bool MalwareOk,
    bool VulnerabilityOk,
    bool SignatureOk,
    bool HumanReviewRecorded,
    bool MonitoredSandboxRunPassed);

/// <summary>The Verified tag decision for one module version.</summary>
/// <param name="Version">The module version.</param>
/// <param name="Verified">True when this version's checks all pass and no earlier or same version has revoked the tag.</param>
/// <param name="FailedChecks">The names of this version's checks that did not pass, in a fixed order.</param>
/// <param name="RevokedBy">The first version in the line whose checks failed, when the tag is revoked from here on. Null when not revoked.</param>
public sealed record VerifiedTagDecision(
    string Version,
    bool Verified,
    IReadOnlyList<string> FailedChecks,
    string? RevokedBy);

/// <summary>
/// The Verified tag as a pure function over recorded check results (doc 48 section 3). The tag needs every automated and
/// human check plus a monitored sandbox run. A failing version revokes the tag from that version onward in its module line.
/// The tag never bypasses permission approval; it is information for the owner's decision.
/// </summary>
public static class VerifiedTagChecks
{
    /// <summary>
    /// Evaluates one module line. The input must be ordered oldest version first. Each version is judged on its own checks,
    /// and the first failing version revokes the tag from that version onward.
    /// </summary>
    public static IReadOnlyList<VerifiedTagDecision> Evaluate(IReadOnlyList<VerifiedTagResults> lineOldestFirst)
    {
        ArgumentNullException.ThrowIfNull(lineOldestFirst);

        var decisions = new List<VerifiedTagDecision>(lineOldestFirst.Count);
        string? revokedBy = null;
        foreach (var results in lineOldestFirst)
        {
            ArgumentNullException.ThrowIfNull(results);
            var failed = FailedChecks(results);
            if (failed.Count > 0)
            {
                revokedBy ??= results.Version;
            }

            var verified = failed.Count == 0 && revokedBy is null;
            decisions.Add(new VerifiedTagDecision(results.Version, verified, failed, revokedBy));
        }

        return decisions;
    }

    /// <summary>The names of the checks that did not pass, in the fixed order of the doc.</summary>
    public static IReadOnlyList<string> FailedChecks(VerifiedTagResults results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var failed = new List<string>();
        if (!results.LicenceOk)
        {
            failed.Add("licence");
        }

        if (!results.MalwareOk)
        {
            failed.Add("malware");
        }

        if (!results.VulnerabilityOk)
        {
            failed.Add("vulnerability");
        }

        if (!results.SignatureOk)
        {
            failed.Add("signature");
        }

        if (!results.HumanReviewRecorded)
        {
            failed.Add("human-review");
        }

        if (!results.MonitoredSandboxRunPassed)
        {
            failed.Add("sandbox-run");
        }

        return failed;
    }
}
