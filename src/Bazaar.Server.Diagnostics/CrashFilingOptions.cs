namespace Bazaar.Server.Diagnostics;

/// <summary>
/// Operator settings the intake reads (doc 43 section 7, <c>telemetry.crash.*</c>). The settings registry fills these in;
/// the module only reads them through <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/>-style injection by the host.
/// </summary>
public sealed record CrashFilingOptions
{
    /// <summary><c>telemetry.crash.issue_repo</c> as <c>owner/name</c>. Empty means filing is off and nothing is sent to GitHub.</summary>
    public string IssueRepo { get; init; } = string.Empty;

    /// <summary><c>telemetry.crash.issues_per_day</c>, 0 to 200. New groups past the budget wait in <c>FilingDeferred</c>.</summary>
    public int IssuesPerDay { get; init; } = 20;

    /// <summary>The name printed in the issue's bot label: the company server that files (doc 21 community rule).</summary>
    public string FiledBy { get; init; } = "the Bazaar company server";

    /// <summary>Attempts before an outbox row is parked as dead.</summary>
    public int MaxActionAttempts { get; init; } = 10;

    /// <summary>How many times intake recomputes a group after a version conflict before it gives up.</summary>
    public int MaxCommitAttempts { get; init; } = 8;

    /// <summary>True when an issue repository is set and the budget allows at least one filing a day.</summary>
    public bool FilingEnabled => !string.IsNullOrWhiteSpace(IssueRepo) && IssuesPerDay > 0;
}
