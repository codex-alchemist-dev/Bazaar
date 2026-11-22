namespace Bazaar.Admin;

/// <summary>
/// The result of <c>kodo verify</c> (doc 45 section 1.12). The plain text and the <c>--json</c> document are two renderings of this one
/// model. It holds no secret: the checks report messages that name keys and rules, never values.
/// </summary>
public sealed class KodoVerifyReport
{
    /// <summary>Always <c>verify</c>. A usage error is an <see cref="AdminReport"/> instead.</summary>
    public string Command { get; init; } = "verify";

    /// <summary>The exit code: 0 green, 1 invalid input, 2 amber (held for acknowledgement), 3 red.</summary>
    public int ExitCode { get; init; }

    /// <summary>green, amber, red or invalid-input. A validator hold makes a green report amber.</summary>
    public string Result { get; init; } = "";

    /// <summary>The 0 to 100 score. Null when the file has errors, because no checks ran.</summary>
    public int? Score { get; init; }

    /// <summary>The plan hash. Null when the file has errors.</summary>
    public string? PlanHash { get; init; }

    /// <summary>Every check, in report order. Empty when the file has errors.</summary>
    public List<AdminCheck> Checks { get; init; } = [];

    /// <summary>The ids of the checks that hold the overall status at amber, and that setup needs acknowledged.</summary>
    public List<string> AmberItems { get; init; } = [];

    /// <summary>Loader errors and holds, each with its key path and rule. Never a value.</summary>
    public List<AdminIssue> Issues { get; init; } = [];
}

/// <summary>One verification check's row. <see cref="Status"/> is green, amber, red or not-evaluated.</summary>
public sealed class AdminCheck
{
    public string Id { get; init; } = "";

    public string Title { get; init; } = "";

    public string Status { get; init; } = "";

    public string Message { get; init; } = "";

    public string? Remediation { get; init; }
}
