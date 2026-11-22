namespace Bazaar.Contracts.Coding;

/// <summary>
/// One row of the <c>check_runs</c> table: a server-owned verification run on a commit
/// (docs/design/17-coding-agent-harness.md, data). Flags are <see cref="CheckFlag"/> codes.
/// <paramref name="Verdict"/> and <paramref name="ToolExitCodes"/> are PROPOSED additions (doc 17, check_runs): the server-computed
/// pass/fail result (a <see cref="CheckVerdict"/> code) and one exit code per tool the run used, keyed by a short tool name such as
/// <c>build</c>, <c>test</c>, <c>luau-analyze</c> or <c>selene</c>. A null exit code means the tool did not run. Both are null on
/// rows written before they existed, and the verdict is never taken from an agent report.
/// </summary>
public sealed record CheckRunRecord(
    Guid Id,
    string Commit,
    string? BaseCommit,
    int? BuildExitCode,
    int? TestsPassed,
    int? TestsFailed,
    int? TestsSkipped,
    int? TestCountBase,
    IReadOnlyList<string> Flags,
    DateTimeOffset RanAt,
    string? Verdict = null,
    IReadOnlyDictionary<string, int?>? ToolExitCodes = null);

/// <summary>The server-computed overall result of a <see cref="CheckRunRecord"/>.</summary>
public static class CheckVerdict
{
    /// <summary>Every tool that ran exited zero and no blocking flag was raised.</summary>
    public const string Pass = "pass";

    /// <summary>A tool failed, a test failed, or a blocking flag was raised.</summary>
    public const string Fail = "fail";

    /// <summary>The run could not finish (timeout, crash, missing tool). Never treated as a pass.</summary>
    public const string Error = "error";

    /// <summary>True when <paramref name="verdict"/> is one of the known codes.</summary>
    public static bool IsKnown(string? verdict) => verdict is Pass or Fail or Error;
}
