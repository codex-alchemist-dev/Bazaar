using Bazaar.Install.Trust;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// Maps a sandbox run to the Verified tag's <c>MonitoredSandboxRun</c> check state (doc 48 sections 2.1 rule 2 and 3). Only a clean run
/// passes. A violation or a replay mismatch fails. A run that did not finish judging (refused, timed out, errored) stays pending, never passed.
/// The run must also be bound to the version it ran for: a result for another module version never counts.
/// </summary>
public static class VerifiedTagSandboxCheck
{
    /// <summary>The check state for <paramref name="moduleVersion"/> given the run that was recorded for <paramref name="spec"/>.</summary>
    public static CheckState Evaluate(SandboxRunSpec spec, SandboxRunResult? result, string moduleVersion)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(moduleVersion);

        if (result is null
            || !string.Equals(spec.ModuleVersion, moduleVersion, StringComparison.Ordinal)
            || !string.Equals(spec.RunId, result.RunId, StringComparison.Ordinal))
        {
            return CheckState.Pending;
        }

        return result.Verdict switch
        {
            SandboxVerdict.Clean when result.Violations.Count == 0 => CheckState.Passed,
            SandboxVerdict.UndeclaredAccess or SandboxVerdict.CanaryTriggered or SandboxVerdict.ReplayMismatch => CheckState.Failed,
            _ => CheckState.Pending,
        };
    }
}
