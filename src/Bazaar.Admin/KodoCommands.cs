using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// The <c>kodo verify</c> command (doc 45 section 1.12). It reads the file with the setup loader, so validation is the same as
/// <c>setup validate</c>, then runs the read-only verifier. It writes nothing and never calls the applier.
/// </summary>
internal static class KodoCommands
{
    /// <summary>
    /// Loads the file. Errors exit 1 with no checks. Otherwise the checks run, and the exit code follows
    /// <see cref="KodoVerifyExitCodes.For"/>, with a validator hold counted as amber.
    /// </summary>
    public static async Task<KodoVerifyReport> VerifyAsync(string configPath, CancellationToken cancellationToken)
    {
        var read = SetupOrchestrator.ReadConfig(configPath);
        if (read.Issue is not null)
        {
            return Refused(read.Issue);
        }

        var load = SetupPlanLoader.Load(read.Text!);
        if (load.Plan is null)
        {
            return Refused(load.Issues.Select(SetupChangeView.ToIssue).ToList());
        }

        var verifier = new SetupVerifier(SetupVerificationChecks.All);
        var report = await verifier.VerifyAsync(new SetupVerificationContext(load.Plan), cancellationToken);
        var hasHolds = load.Issues.Any(issue => issue.Severity == SetupIssueSeverity.Hold);
        var exitCode = KodoVerifyExitCodes.For(report.Overall, hasHolds);

        return new KodoVerifyReport
        {
            ExitCode = exitCode,
            Result = ResultFor(exitCode),
            Score = report.Score,
            PlanHash = load.PlanHash,
            Checks = report.Checks.Select(ToAdminCheck).ToList(),
            AmberItems = report.AmberItems.Select(item => item.Id).ToList(),
            Issues = load.Issues.Select(SetupChangeView.ToIssue).ToList(),
        };
    }

    /// <summary>A report for input that could not be read or loaded. No checks ran, so there is no score.</summary>
    private static KodoVerifyReport Refused(List<SetupReportIssue> issues) => new()
    {
        ExitCode = KodoVerifyExitCodes.InvalidInput,
        Result = ResultFor(KodoVerifyExitCodes.InvalidInput),
        Issues = issues,
    };

    private static KodoVerifyReport Refused(SetupReportIssue issue) => Refused([issue]);

    private static AdminCheck ToAdminCheck(SetupCheckReport row) => new()
    {
        Id = row.Id,
        Title = row.Title,
        Status = row.Status switch
        {
            SetupCheckStatus.Green => "green",
            SetupCheckStatus.Amber => "amber",
            SetupCheckStatus.Red => "red",
            SetupCheckStatus.NotEvaluated => "not-evaluated",
            _ => throw new ArgumentOutOfRangeException(nameof(row), row.Status, "Unknown check status."),
        },
        Message = row.Message,
        Remediation = row.Remediation,
    };

    /// <summary>The result word for an exit code.</summary>
    internal static string ResultFor(int exitCode) => exitCode switch
    {
        KodoVerifyExitCodes.Green => "green",
        KodoVerifyExitCodes.Amber => "amber",
        KodoVerifyExitCodes.Red => "red",
        _ => "invalid-input",
    };
}
