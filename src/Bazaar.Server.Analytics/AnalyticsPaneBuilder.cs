using System.Text.Json;
using Bazaar.Contracts.Panes;

namespace Bazaar.Server.Analytics;

/// <summary>
/// Turns a report into the declarative pane the client lays out (doc 20). Model:
/// <c>{ "windowDays": n, "company": {...}, "agents": [ { "agentId", "tasksCompleted", "tasksPerWeek", "reviewPassRate", "revertRate", "hints": [] } ] }</c>.
/// A rate with no samples is JSON null.
/// </summary>
public static class AnalyticsPaneBuilder
{
    public static PaneDescriptor Build(AnalyticsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteNumber("windowDays", Math.Round(report.Window.Length.TotalDays, 1));
            w.WriteStartObject("company");
            w.WriteNumber("tasksCompleted", report.Company.TasksCompleted);
            w.WriteNumber("tasksPerWeek", Math.Round(report.Company.TasksPerWeek, 2));
            Rate(w, "reviewPassRate", report.Company.ReviewPassRate);
            Rate(w, "revertRate", report.Company.RevertRate);
            w.WriteEndObject();
            w.WriteStartArray("agents");
            foreach (var a in report.Agents)
            {
                w.WriteStartObject();
                w.WriteString("agentId", a.AgentId);
                w.WriteNumber("tasksCompleted", a.TasksCompleted);
                w.WriteNumber("tasksPerWeek", Math.Round(a.TasksPerWeek, 2));
                Rate(w, "reviewPassRate", a.ReviewPassRate);
                Rate(w, "revertRate", a.RevertRate);
                w.WriteStartArray("hints");
                foreach (var h in a.CompanyHints)
                {
                    w.WriteStringValue(h);
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return new PaneDescriptor(PaneKinds.Analytics, "Company analytics", string.Empty, doc.RootElement.Clone());
    }

    private static void Rate(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, Math.Round(v, 3));
        }
        else
        {
            w.WriteNull(name);
        }
    }
}
