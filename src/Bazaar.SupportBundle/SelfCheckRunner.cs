using System.Text;
using System.Text.Json;

namespace Bazaar.SupportBundle;

/// <summary>Runs self-checks and writes the report (doc 37 section 4d). A check that throws fails; it never stops the others.</summary>
public static class SelfCheckRunner
{
    /// <summary>Runs every check, sorted by id, and returns the report.</summary>
    public static SelfCheckReport Run(string version, IEnumerable<ISelfCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var results = new List<SelfCheckResult>();
        foreach (var check in checks.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            try
            {
                results.Add(check.Run());
            }
            catch (Exception ex)
            {
                // Only the exception type is kept: its message can carry paths or user names.
                results.Add(new SelfCheckResult(check.Id, false, $"threw {ex.GetType().Name}"));
            }
        }

        return new SelfCheckReport(version, results);
    }

    /// <summary>Writes the report as indented JSON, schema <c>bazaar.selfcheck/1</c>. The same report gives the same text.</summary>
    public static string ToJson(SelfCheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", SelfCheckReport.Schema);
            w.WriteString("version", report.Version);
            w.WriteBoolean("passed", report.Passed);
            w.WriteStartArray("checks");
            foreach (var c in report.Checks)
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteBoolean("passed", c.Passed);
                w.WriteString("reason", c.Reason);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
