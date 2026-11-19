using System.Globalization;
using System.Text;
using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;

namespace Bazaar.Kodo.Export;

/// <summary>
/// Renders Kodo's metric families in the Prometheus text exposition format (doc 46 section 15.4). Pure: the caller serves the text
/// on the metrics listener, which is loopback by default and needs a bearer token when it is not.
/// </summary>
public static class KodoPrometheusRenderer
{
    /// <summary>The media type of the exposition text.</summary>
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>Renders the families. Every family is a gauge, with one HELP and one TYPE line.</summary>
    /// <exception cref="ArgumentException">A family name, a label name or a value is not one the export writes.</exception>
    public static string Render(IEnumerable<KodoMetricFamily> families)
    {
        ArgumentNullException.ThrowIfNull(families);

        StringBuilder text = new();
        foreach (KodoMetricFamily family in families)
        {
            if (!KodoMetricNames.All.Contains(family.Name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"'{family.Name}' is not a Kodo metric name.", nameof(families));
            }

            string name = KodoMetricNames.ToPrometheus(family.Name);
            text.Append("# HELP ").Append(name).Append(' ').Append(EscapeHelp(family.Description)).Append('\n');
            text.Append("# TYPE ").Append(name).Append(" gauge\n");

            foreach (KodoMetricPoint point in family.Points)
            {
                text.Append(name);
                AppendLabels(text, point.Labels);
                text.Append(' ').Append(FormatValue(point.Value)).Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>Renders the metric families of one snapshot.</summary>
    /// <param name="snapshot">The status snapshot.</param>
    /// <param name="moduleIdOf">The module that owns a component id. Defaults to Kodo.</param>
    public static string RenderSnapshot(KodoStatusSnapshotDto snapshot, Func<string, string>? moduleIdOf = null) =>
        Render(KodoMetricFamilies.From(snapshot, moduleIdOf));

    /// <summary>Escapes a label value: backslash, double quote and line feed (Prometheus text format).</summary>
    public static string EscapeLabelValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    /// <summary>Escapes help text: backslash and line feed. Quotes are allowed in help text.</summary>
    public static string EscapeHelp(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private static void AppendLabels(StringBuilder text, IReadOnlyList<KodoMetricLabel> labels)
    {
        if (labels.Count == 0)
        {
            return;
        }

        text.Append('{');
        for (int i = 0; i < labels.Count; i++)
        {
            KodoMetricLabel label = labels[i];
            if (!KodoMetricLabels.Allowed.Contains(label.Name, StringComparer.Ordinal))
            {
                throw new ArgumentException($"'{label.Name}' is not a label the export may write.", nameof(labels));
            }

            if (i > 0)
            {
                text.Append(',');
            }

            text.Append(label.Name).Append("=\"").Append(EscapeLabelValue(label.Value)).Append('"');
        }

        text.Append('}');
    }

    private static string FormatValue(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("A metric value must be finite.", nameof(value));
        }

        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
