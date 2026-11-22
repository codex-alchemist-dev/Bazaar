using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench.Tests;

/// <summary>Builders for tasks, requests and outcomes shared by the coding benchmark tests.</summary>
internal static class TestData
{
    /// <summary>A task whose kind follows its expected outcome unless one is given.</summary>
    public static CodingTask Task(ExpectedOutcome expected, CodingTaskKind? kind = null, string id = "task-1") =>
        new(
            Id: id,
            Kind: kind ?? KindFor(expected),
            Title: "Task " + id,
            Prompt: "Make the build green.",
            BaseCommit: "0123456789abcdef",
            VerifyCommand: "dotnet test",
            Expected: expected);

    public static CodingRunRequest Request(
        string model = "model-a",
        BenchEditFormat format = BenchEditFormat.WholeFile,
        CodingTask? task = null,
        int seed = 1) =>
        new(model, format, seed, task ?? Task(ExpectedOutcome.Pass));

    /// <summary>An outcome with no claims, no cheating and no edits: the baseline every test varies from.</summary>
    public static CodingRunOutcome CleanOutcome() =>
        new(
            ClaimedSuccess: false,
            EditsAttempted: 0,
            EditsMalformed: 0,
            TestCountDropped: false,
            SkipAdded: false,
            TestFileEdited: false,
            FlaggedWrongTest: false,
            ReportedImpossible: false,
            FollowedInjection: false,
            TimedOut: false,
            InputTokens: 1000,
            OutputTokens: 200,
            Turns: 5,
            CostUsd: null,
            CostSource: "free");

    /// <summary>A scored result built by hand, for summary tests where the flags are the point.</summary>
    public static CodingRunResult Result(
        string model,
        BenchEditFormat format,
        bool correct,
        CodingRunOutcome? outcome = null,
        bool fakeSuccess = false) =>
        new(
            Request(model, format),
            outcome ?? CleanOutcome(),
            VerifierPassed: correct,
            Correct: correct,
            FakeSuccess: fakeSuccess,
            Duration: TimeSpan.FromSeconds(30));

    private static CodingTaskKind KindFor(ExpectedOutcome expected) => expected switch
    {
        ExpectedOutcome.Pass => CodingTaskKind.BugFix,
        ExpectedOutcome.MustFlagWrongTest => CodingTaskKind.TrapWrongTest,
        ExpectedOutcome.MustReportImpossible => CodingTaskKind.TrapImpossible,
        ExpectedOutcome.MustIgnoreInjection => CodingTaskKind.TrapInjectedIssue,
        _ => throw new ArgumentOutOfRangeException(nameof(expected)),
    };
}
