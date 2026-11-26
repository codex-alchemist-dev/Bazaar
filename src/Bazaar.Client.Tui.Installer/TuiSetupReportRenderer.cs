using System.Globalization;
using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Installer;

/// <summary>
/// Lays a <see cref="SetupReport"/> out as the lines the installer TUI shows (doc 58 work package 3). It reads only the report's own
/// display text, in which a secret-typed value is already hidden by <see cref="SetupChangeView"/>, so it can never print one. Every
/// result is a word, not only a colour, and the lines read in order for a screen reader on the plain path.
/// </summary>
public sealed class TuiSetupReportRenderer : ISetupReportRenderer
{
    public IReadOnlyList<string> RenderLines(SetupReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"Result: {report.Result} (exit {report.ExitCode})"),
            report.PlanHash is null ? "Plan hash: not computed, because the answers have errors." : $"Plan hash: {report.PlanHash}",
        };

        if (report.Outcome == SetupOrchestrator.ApplyUnavailableOutcome)
        {
            lines.Add(SetupOrchestrator.ApplyUnavailableMessage);
        }
        else
        {
            foreach (var issue in report.Issues)
            {
                lines.Add($"{(issue.Severity == "hold" ? "Hold" : "Error")} at {issue.Path}: {issue.Message}");
            }
        }

        if (report.Command == "dry-run")
        {
            lines.Add(report.PlanHash is null ? "Changes: not computed." : string.Create(CultureInfo.InvariantCulture, $"Changes: {report.Changes.Count}"));
            lines.AddRange(report.Changes.Select(ChangeLine));
            AddUnapplied(report, lines, "An apply would be held");
            lines.Add("Writes: none.");
        }
        else if (report.Command == "apply" && report.Outcome != SetupOrchestrator.ApplyUnavailableOutcome)
        {
            AddUnapplied(report, lines, "Held");
            lines.Add(report.AppliedSteps.Count == 0 ? "Applied steps: none." : $"Applied steps: {string.Join(", ", report.AppliedSteps)}");
        }

        return lines;
    }

    private static string ChangeLine(SetupReportChange change) => change.Kind switch
    {
        "create" => $"  create {change.Path} = {change.Planned}",
        "remove" => $"  remove {change.Path} (was {change.Current})",
        _ => $"  update {change.Path}: {change.Current} -> {change.Planned}",
    };

    private static void AddUnapplied(SetupReport report, List<string> lines, string lead)
    {
        if (report.UnappliedSections.Count > 0)
        {
            lines.Add($"{lead}: no registered step applies these plan sections: {SetupPlanSections.Describe(SetupPlanSections.ByIds(report.UnappliedSections))}.");
        }
    }
}
