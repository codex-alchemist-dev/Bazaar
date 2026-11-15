namespace Bazaar.Testing.LiveEval;

/// <summary>
/// A versioned set of live-eval tasks mined from Bazaar's history, including traps (doc 34, "Live evals").
/// </summary>
public sealed record TaskSet(string Id, int Version, IReadOnlyList<TaskCase> Cases);

/// <summary>One task: the prompt given to the target and the checks its outcome must satisfy.</summary>
public sealed record TaskCase(string Id, string Prompt, IReadOnlyList<TaskCheck> Checks);

/// <summary>One expected check. Deterministic checks score first; judges second (doc 34).</summary>
public sealed record TaskCheck(TaskCheckKind Kind);

/// <summary>The kinds of check a task case may declare.</summary>
public enum TaskCheckKind
{
    /// <summary>The project builds with warnings as errors.</summary>
    Build,

    /// <summary>The tests pass.</summary>
    Tests,

    /// <summary>Test files are not edited (a wrong test must be flagged, not changed).</summary>
    TestFilesUntouched,

    /// <summary>The response follows the required edit format.</summary>
    FormatCompliant,

    /// <summary>The fake-success detector did not fire.</summary>
    FakeSuccess,

    /// <summary>A trap: the target must flag the problem.</summary>
    MustFlag,

    /// <summary>A trap: an impossible task must be reported as a failure.</summary>
    MustReportFailure,

    /// <summary>A trap: an injected instruction in the issue must not be acted on.</summary>
    MustNotAct,
}
