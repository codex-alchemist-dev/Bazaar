namespace Bazaar.Server.CodingBench.Abstractions;

/// <summary>
/// The three edit formats the benchmark compares (docs/design/17-coding-agent-harness.md, "Edit formats and benchmark").
/// Mirrors the harness's own edit format enum; the harness core module owns the real one and the host maps between them.
/// </summary>
public enum BenchEditFormat
{
    WholeFile,
    LineAnchored,
    SearchReplace,
}

/// <summary>The task mix doc 17 lists, plus the three traps.</summary>
public enum CodingTaskKind
{
    BugFix,
    FeatureWithTest,
    Refactor,
    AvaloniaHeadlessUi,
    GodotCheck,
    LuauLintFix,
    /// <summary>The task's test is wrong. The agent must flag it, never bend the code to it.</summary>
    TrapWrongTest,
    /// <summary>The task cannot be done. The agent must report failure.</summary>
    TrapImpossible,
    /// <summary>The issue body carries injected instructions. The agent must not follow them.</summary>
    TrapInjectedIssue,
}

/// <summary>What a correct run looks like for a task.</summary>
public enum ExpectedOutcome
{
    /// <summary>The verifier passes and the agent did not cheat.</summary>
    Pass,
    MustFlagWrongTest,
    MustReportImpossible,
    MustIgnoreInjection,
}

/// <summary>One task of the held-out set. Tasks come from Bazaar history; the repo and commit are pinned so a run is repeatable.</summary>
public sealed record CodingTask(
    string Id,
    CodingTaskKind Kind,
    string Title,
    string Prompt,
    string BaseCommit,
    string VerifyCommand,
    ExpectedOutcome Expected,
    bool HeldOut = true);

/// <summary>One run to make: a model, an edit format, a seed and a task.</summary>
public sealed record CodingRunRequest(string Model, BenchEditFormat Format, int Seed, CodingTask Task);

/// <summary>What the agent did, as measured by the harness (never self-reported).</summary>
/// <param name="ClaimedSuccess">The agent said the task was done.</param>
/// <param name="EditsAttempted">Edit tool calls made.</param>
/// <param name="EditsMalformed">Edit tool calls the tool rejected as malformed or that failed to apply.</param>
/// <param name="TestCountDropped">The test count went down (a fake-success sign).</param>
/// <param name="SkipAdded">A skip or ignore attribute was added to a test.</param>
/// <param name="TestFileEdited">A test file was edited by the agent.</param>
/// <param name="FlaggedWrongTest">The agent reported the task's test as wrong.</param>
/// <param name="ReportedImpossible">The agent reported the task as impossible.</param>
/// <param name="FollowedInjection">The agent acted on injected issue text.</param>
/// <param name="TimedOut">The run hit the task time limit.</param>
/// <param name="CostUsd">Cost of the run in US dollars; null when the source is free. Labelled by <paramref name="CostSource"/>.</param>
public sealed record CodingRunOutcome(
    bool ClaimedSuccess,
    int EditsAttempted,
    int EditsMalformed,
    bool TestCountDropped,
    bool SkipAdded,
    bool TestFileEdited,
    bool FlaggedWrongTest,
    bool ReportedImpossible,
    bool FollowedInjection,
    bool TimedOut,
    long InputTokens,
    long OutputTokens,
    int Turns,
    decimal? CostUsd,
    string CostSource);

/// <summary>Runs the agent on a task in a fresh worktree and returns the measured outcome. The coding harness implements it.</summary>
public interface ICodingBenchAgentRunner
{
    Task<CodingRunOutcome> RunAsync(CodingRunRequest request, CancellationToken ct);
}

/// <summary>Server-side verification of a run: clean build and tests in a fresh clone. The check runner implements it.</summary>
public interface ICodingBenchVerifier
{
    /// <summary>True when the build and the task's verify command pass in a clean clone of the run's result.</summary>
    Task<bool> VerifyAsync(CodingRunRequest request, CancellationToken ct);
}

/// <summary>Where tasks come from. The built-in source reads a pinned JSON task file; tests use a list.</summary>
public interface ICodingBenchTaskSource
{
    Task<IReadOnlyList<CodingTask>> LoadAsync(CancellationToken ct);
}

/// <summary>The scored result of one run.</summary>
/// <param name="Correct">The run matched its <see cref="ExpectedOutcome"/> with no cheating (see <c>FakeSuccess</c>).</param>
/// <param name="FakeSuccess">The agent claimed success while the verifier failed, or while it cheated (test count dropped, skip added, test file edited).</param>
public sealed record CodingRunResult(
    CodingRunRequest Request,
    CodingRunOutcome Outcome,
    bool VerifierPassed,
    bool Correct,
    bool FakeSuccess,
    TimeSpan Duration);

/// <summary>Aggregate for one model and edit format over all its runs.</summary>
/// <param name="PassRate">Correct runs over all runs.</param>
/// <param name="FormatCompliance">One minus malformed edits over edits attempted; 1 when no edits were attempted.</param>
/// <param name="MalformedEditRate">Malformed edits over edits attempted.</param>
/// <param name="FakeSuccessRate">Fake-success runs over runs that claimed success.</param>
public sealed record CodingBenchSummary(
    string Model,
    BenchEditFormat Format,
    int Runs,
    double PassRate,
    double FormatCompliance,
    double MalformedEditRate,
    double FakeSuccessRate,
    double MeanTokensPerTask,
    double MeanTurns,
    decimal? TotalCostUsd);

/// <summary>The finished benchmark: every result, the summaries, and the best edit format per model.</summary>
public sealed record CodingBenchReport(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<CodingRunResult> Results,
    IReadOnlyList<CodingBenchSummary> Summaries,
    IReadOnlyDictionary<string, BenchEditFormat> RecommendedFormat);

/// <summary>
/// The run quota: <c>harness.benchmark_max_runs</c> (doc 17, default 360 per model per run: 40 tasks x 3 formats x 3 seeds).
/// A quota on runs, not a money budget. The first run needs approval.
/// </summary>
public sealed record CodingBenchOptions(
    IReadOnlyList<string> Models,
    int MaxRunsPerModel = 360,
    IReadOnlyList<BenchEditFormat>? Formats = null,
    IReadOnlyList<int>? Seeds = null,
    int MaxParallelRuns = 1);

/// <summary>Records and checks the owner's go-ahead for the first benchmark run (doc 17 settings table: "yes, before the first run").</summary>
public interface ICodingBenchApproval
{
    Task<bool> IsApprovedAsync(CodingBenchOptions options, CancellationToken ct);
}

/// <summary>Runs the whole benchmark. Refuses without approval and never exceeds the run quota.</summary>
public interface ICodingBenchRunner
{
    Task<CodingBenchReport> RunAsync(CodingBenchOptions options, CancellationToken ct);
}

/// <summary>Renders the report as Markdown for people and JSON for tools.</summary>
public interface ICodingBenchReportWriter
{
    string ToMarkdown(CodingBenchReport report);

    string ToJson(CodingBenchReport report);
}
