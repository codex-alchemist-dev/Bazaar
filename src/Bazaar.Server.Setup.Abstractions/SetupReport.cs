namespace Bazaar.Server.Setup;

/// <summary>
/// One setup command's result, as data. Front ends (the CLI, a server terminal UI) render it as they like; this model holds no
/// text layout. A secret-typed value is never stored here: the change view hides it before the model is built.
/// </summary>
public sealed class SetupReport
{
    /// <summary>validate, dry-run, apply or usage (an argument error, before any file was read).</summary>
    public string Command { get; init; } = "";

    /// <summary>The exit code, from <see cref="SetupExitCode"/>.</summary>
    public int ExitCode { get; init; }

    /// <summary>valid, invalid-input, held, applied or apply-failed-rolled-back.</summary>
    public string Result { get; init; } = "";

    /// <summary>
    /// The specific outcome when a result needs one more word. apply_unavailable: the apply gate is closed, so the applier was not
    /// run. unapplied_sections: the plan asks for sections no registered step applies, so nothing was applied. Null otherwise.
    /// </summary>
    public string? Outcome { get; init; }

    /// <summary>The plan hash. Null when the input has errors, because no plan was built.</summary>
    public string? PlanHash { get; init; }

    /// <summary>empty for a dry run or apply, which compare against an empty snapshot. Null for validate.</summary>
    public string? CurrentState { get; init; }

    /// <summary>Errors and holds, each with the key path and the rule. Never a value.</summary>
    public List<SetupReportIssue> Issues { get; init; } = [];

    /// <summary>Dry-run changes, sorted by path.</summary>
    public List<SetupReportChange> Changes { get; init; } = [];

    /// <summary>
    /// Plan sections the plan asks for that no registered step applies (ids, never values). A dry run lists what an apply would
    /// refuse; an apply that was held for them lists them too. Empty when every requested section is covered.
    /// </summary>
    public List<string> UnappliedSections { get; init; } = [];

    /// <summary>Steps that completed, in run order.</summary>
    public List<string> AppliedSteps { get; init; } = [];
}

/// <summary>One error or hold. <see cref="Path"/> is a dotted key path, or a flag name for a usage error.</summary>
public sealed class SetupReportIssue
{
    /// <summary>error or hold.</summary>
    public string Severity { get; init; } = "";

    public string Path { get; init; } = "";

    public string Message { get; init; } = "";
}

/// <summary>One dry-run change. <see cref="Current"/> and <see cref="Planned"/> are display text, with secrets hidden.</summary>
public sealed class SetupReportChange
{
    public string Path { get; init; } = "";

    /// <summary>create, update or remove.</summary>
    public string Kind { get; init; } = "";

    public string? Current { get; init; }

    public string? Planned { get; init; }
}
