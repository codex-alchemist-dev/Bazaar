namespace Bazaar.Admin;

/// <summary>
/// Plain text output (doc 45 section 1.7): one fact per line, in order, with no colour and no box drawing, so a screen reader
/// reads it straight through.
/// </summary>
internal static class AdminTextRenderer
{
    public static void Write(AdminReport report, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine($"Command: {CommandText(report.Command)}");
        writer.WriteLine($"Result: {ResultText(report.Result)} (exit {report.ExitCode})");
        if (report.CurrentState == SetupCommands.EmptyState)
        {
            writer.WriteLine("Current state: empty install. No state reader is built yet, so every planned value is compared with nothing.");
        }

        writer.WriteLine(report.PlanHash is null
            ? "Plan hash: not computed, because the file has errors."
            : $"Plan hash: {report.PlanHash}");

        if (report.Outcome == SetupCommands.ApplyUnavailableOutcome)
        {
            // The gate's reason is one plain line, not a key-and-rule issue.
            writer.WriteLine(SetupCommands.ApplyUnavailableMessage);
        }
        else if (report.Issues.Count == 0)
        {
            writer.WriteLine("Issues: none.");
        }

        if (report.Outcome != SetupCommands.ApplyUnavailableOutcome)
        {
            foreach (var issue in report.Issues)
            {
                var label = issue.Severity == "hold" ? "Hold" : "Error";
                writer.WriteLine($"{label} at {issue.Path}: {issue.Message}");
            }
        }

        if (report.Command == "dry-run")
        {
            writer.WriteLine(report.PlanHash is null ? "Changes: not computed." : $"Changes: {report.Changes.Count}");
            foreach (var change in report.Changes)
            {
                writer.WriteLine(ChangeLine(change));
            }

            writer.WriteLine("Writes: none.");
        }

        if (report.Command == "apply" && report.Outcome != SetupCommands.ApplyUnavailableOutcome)
        {
            writer.WriteLine(report.AppliedSteps.Count == 0
                ? "Applied steps: none."
                : $"Applied steps: {string.Join(", ", report.AppliedSteps)}");
        }
    }

    private static string ChangeLine(AdminChange change) => change.Kind switch
    {
        "create" => $"Create {change.Path} = {change.Planned}",
        "update" => $"Update {change.Path}: from {change.Current} to {change.Planned}",
        _ => $"Remove {change.Path} (was {change.Current})",
    };

    private static string CommandText(string command) => command switch
    {
        "validate" => "setup validate",
        "dry-run" => "setup --dry-run",
        "apply" => "setup apply",
        _ => "usage error",
    };

    private static string ResultText(string result) => result switch
    {
        "valid" => "valid, with no errors or holds",
        "invalid-input" => "invalid input",
        "held" => "held, so nothing was changed",
        "applied" => "applied",
        "apply-failed-rolled-back" => "apply failed and was rolled back",
        _ => result,
    };
}
