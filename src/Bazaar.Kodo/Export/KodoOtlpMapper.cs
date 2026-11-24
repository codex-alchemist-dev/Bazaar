using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;

namespace Bazaar.Kodo.Export;

/// <summary>One attribute of an OTLP data point, with its OpenTelemetry key.</summary>
/// <param name="Key">The attribute key, dotted as OpenTelemetry spells it.</param>
/// <param name="Value">The attribute value.</param>
public sealed record KodoOtlpKeyValue(string Key, string Value);

/// <summary>One OTLP number data point.</summary>
/// <param name="TimeUnixNano">The time of the sample, in nanoseconds since the Unix epoch.</param>
/// <param name="AsDouble">The sample value.</param>
/// <param name="Attributes">The attributes of the sample.</param>
public sealed record KodoOtlpDataPoint(long TimeUnixNano, double AsDouble, IReadOnlyList<KodoOtlpKeyValue> Attributes);

/// <summary>An OTLP gauge metric.</summary>
/// <param name="Name">The metric name, dotted.</param>
/// <param name="Description">The description.</param>
/// <param name="Unit">The unit.</param>
/// <param name="DataPoints">The data points.</param>
public sealed record KodoOtlpGauge(string Name, string Description, string Unit, IReadOnlyList<KodoOtlpDataPoint> DataPoints);

/// <summary>
/// Maps Kodo's metric families to OTLP-shaped gauge records (doc 46 section 15.4). These are plain records, not the OpenTelemetry SDK:
/// a later adapter that uses doc 23's pipeline turns them into OTLP messages. The attribute keys follow the same allow list as the
/// Prometheus labels.
/// </summary>
public static class KodoOtlpMapper
{
    /// <summary>Maps one snapshot. Every point of a snapshot shares the snapshot's <see cref="KodoStatusDto.AsOf"/>.</summary>
    /// <param name="snapshot">The status snapshot.</param>
    /// <param name="moduleIdOf">The module that owns a component id. Defaults to Kodo.</param>
    public static IReadOnlyList<KodoOtlpGauge> Map(KodoStatusSnapshotDto snapshot, Func<string, string>? moduleIdOf = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long time = UnixNano(snapshot.Status.AsOf);

        List<KodoOtlpGauge> gauges = [];
        foreach (KodoMetricFamily family in KodoMetricFamilies.From(snapshot, moduleIdOf))
        {
            List<KodoOtlpDataPoint> points = [];
            foreach (KodoMetricPoint point in family.Points)
            {
                List<KodoOtlpKeyValue> attributes = [];
                foreach (KodoMetricLabel label in point.Labels)
                {
                    attributes.Add(new KodoOtlpKeyValue(AttributeKey(label.Name), label.Value));
                }

                points.Add(new KodoOtlpDataPoint(time, point.Value, attributes));
            }

            gauges.Add(new KodoOtlpGauge(family.Name, family.Description, family.Unit, points));
        }

        return gauges;
    }

    /// <summary>The OpenTelemetry attribute key for an allowed label name.</summary>
    /// <exception cref="ArgumentException">The label is not on the allow list.</exception>
    public static string AttributeKey(string label) => label switch
    {
        KodoMetricLabels.ProbeId => "probe.id",
        KodoMetricLabels.ModuleId => "module.id",
        KodoMetricLabels.State => "state",
        _ => throw new ArgumentException($"'{label}' is not a label the export may write.", nameof(label)),
    };

    // A long holds about 292 years of nanoseconds either side of the epoch, so a time further out is held at the edge, not thrown.
    private static long UnixNano(DateTimeOffset at) =>
        Math.Clamp((at.UtcDateTime - DateTime.UnixEpoch).Ticks, -long.MaxValue / 100, long.MaxValue / 100) * 100;
}
