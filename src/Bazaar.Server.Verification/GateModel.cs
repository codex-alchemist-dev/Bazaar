namespace Bazaar.Server.Verification;

public enum GateCheckKind
{
    CleanBuild,
    TestCount,
    TestEdits,
    Skips,
    Timeouts,
    UiChange,
    BehaviourChange,
}

public enum GateSeverity
{
    Pass,
    Warning,
    Fail,
}

public sealed record GateFinding(GateCheckKind Check, GateSeverity Severity, string Message);

/// <summary>One command the gate (or an agent) ran. <see cref="RecordedSuccess"/> is what the log claims.</summary>
public sealed record CommandRecord(string Command, int ExitCode, bool TimedOut, bool RecordedSuccess, string Output = "");

public sealed record GateRequest(
    string RepoPath,
    string BaseRef,
    string HeadRef,
    string WorkDirectory,
    string BuildExecutable,
    IReadOnlyList<string> BuildArguments,
    TimeSpan BuildTimeout,
    bool ReviewerApprovedTestEdits = false,
    IReadOnlyList<CommandRecord>? SubmittedCommandLog = null);

public sealed record GateReport(IReadOnlyList<GateFinding> Findings, IReadOnlyList<CommandRecord> Commands, int BaseTestCount, int HeadTestCount)
{
    /// <summary>The gate passes only when no finding is a Fail. Warnings do not gate.</summary>
    public bool Passed => Findings.All(f => f.Severity != GateSeverity.Fail);

    public IEnumerable<GateFinding> Failures => Findings.Where(f => f.Severity == GateSeverity.Fail);
}
