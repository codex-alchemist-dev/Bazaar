using Bazaar.Contracts.Kodo;
using Bazaar.Kodo.Api;

namespace Bazaar.Kodo.Export;

/// <summary>One label on a metric point. The name is always one of <see cref="KodoMetricLabels.Allowed"/>.</summary>
/// <param name="Name">The label name, in the Prometheus spelling.</param>
/// <param name="Value">The label value, before escaping.</param>
public sealed record KodoMetricLabel(string Name, string Value);

/// <summary>One sample of a metric family.</summary>
/// <param name="Value">The sample value. Must be finite.</param>
/// <param name="Labels">The labels of the sample. Their names must be in <see cref="KodoMetricLabels.Allowed"/>.</param>
public sealed record KodoMetricPoint(double Value, IReadOnlyList<KodoMetricLabel> Labels);

/// <summary>A gauge family with its dotted OTel-style name, help text, unit and points.</summary>
/// <param name="Name">One of the names in <see cref="KodoMetricNames.All"/>.</param>
/// <param name="Description">The help text.</param>
/// <param name="Unit">The unit. <c>1</c> for a dimensionless value.</param>
/// <param name="Points">The samples.</param>
public sealed record KodoMetricFamily(string Name, string Description, string Unit, IReadOnlyList<KodoMetricPoint> Points);

/// <summary>
/// The only label names the export writes (doc 46 section 15.4). Agent ids and GC ids are never labels: the cardinality rule of doc 23
/// is applied here because the doc 23 pipeline does not exist yet (PROPOSED, doc 46 section 15.7).
/// </summary>
public static class KodoMetricLabels
{
    /// <summary>The probe or category id.</summary>
    public const string ProbeId = "probe_id";

    /// <summary>The module that owns the probe.</summary>
    public const string ModuleId = "module_id";

    /// <summary>A Kodo state word, from <see cref="KodoWireNames"/>.</summary>
    public const string State = "state";

    /// <summary>Every label name the export may write.</summary>
    public static IReadOnlyList<string> Allowed { get; } = [ProbeId, ModuleId, State];
}

/// <summary>The metric names of the Kodo export (doc 46 section 15.4). Names are dotted here and underscored on Prometheus.</summary>
public static class KodoMetricNames
{
    /// <summary>The displayed state, one series per state, 1 for the active one.</summary>
    public const string State = "kodo.state";

    /// <summary>The underlying state shown beside <c>frozen</c>, one series per non-frozen state.</summary>
    public const string UnderlyingState = "kodo.underlying_state";

    /// <summary>The health score from 0 to 100.</summary>
    public const string Score = "kodo.score";

    /// <summary>The status of one probe or category: 0 green, 1 amber, 2 red, 3 unknown.</summary>
    public const string ProbeStatus = "kodo.probe.status";

    /// <summary>Every family name the export may write.</summary>
    public static IReadOnlyList<string> All { get; } = [State, UnderlyingState, Score, ProbeStatus];

    /// <summary>The Prometheus spelling of a dotted name, for example <c>kodo_probe_status</c>.</summary>
    public static string ToPrometheus(string name) => name.Replace('.', '_');
}

/// <summary>
/// Turns a status snapshot into the metric families that both the Prometheus renderer and the OTLP mapping write (doc 46 section 15.4).
/// Pure: it reads only the snapshot and the module lookup it is given.
/// </summary>
public static class KodoMetricFamilies
{
    /// <summary>
    /// The most probe series one snapshot may export (PROPOSED, doc 46 section 15.7). Above it the export refuses rather than
    /// truncating silently, so a scrape never shows a partial fleet as complete.
    /// </summary>
    public const int MaxProbeSeries = 500;

    private static readonly string[] _states = ["healthy", "degraded", "failing", "recovering", "frozen"];
    private static readonly string[] _underlyingStates = ["healthy", "degraded", "failing", "recovering"];

    /// <summary>The families for one snapshot.</summary>
    /// <param name="snapshot">The status snapshot. Its states must be wire strings from <see cref="KodoWireNames"/>.</param>
    /// <param name="moduleIdOf">The module that owns a component id. Defaults to Kodo for every component.</param>
    /// <exception cref="ArgumentException">An id, a module id, a state or a status is not one the export accepts.</exception>
    /// <exception cref="InvalidOperationException">The snapshot has more components than <see cref="MaxProbeSeries"/>.</exception>
    public static IReadOnlyList<KodoMetricFamily> From(KodoStatusSnapshotDto snapshot, Func<string, string>? moduleIdOf = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Func<string, string> moduleOf = moduleIdOf ?? (_ => KodoAssembly.ModuleId);

        if (!_states.Contains(snapshot.Status.State, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{snapshot.Status.State}' is not a displayed state.", nameof(snapshot));
        }

        if (!_underlyingStates.Contains((snapshot.Status.UnderlyingState ?? snapshot.Status.State), StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{(snapshot.Status.UnderlyingState ?? snapshot.Status.State)}' is not an underlying state.", nameof(snapshot));
        }

        if (snapshot.Components.Count > MaxProbeSeries)
        {
            throw new InvalidOperationException(
                $"The snapshot has {snapshot.Components.Count} components, above the {MaxProbeSeries} probe series the export allows.");
        }

        List<KodoMetricPoint> state = [];
        foreach (string name in _states)
        {
            state.Add(new KodoMetricPoint(Flag(name == snapshot.Status.State), [new KodoMetricLabel(KodoMetricLabels.State, name)]));
        }

        List<KodoMetricPoint> underlying = [];
        foreach (string name in _underlyingStates)
        {
            underlying.Add(new KodoMetricPoint(Flag(name == (snapshot.Status.UnderlyingState ?? snapshot.Status.State)), [new KodoMetricLabel(KodoMetricLabels.State, name)]));
        }

        List<KodoMetricPoint> probes = [];
        foreach (KodoComponentDto component in snapshot.Components)
        {
            string id = ValidateComponentId(component.Id);
            string module = ValidateModuleId(moduleOf(component.Id));
            probes.Add(new KodoMetricPoint(
                Severity(component.State),
                [new KodoMetricLabel(KodoMetricLabels.ProbeId, id), new KodoMetricLabel(KodoMetricLabels.ModuleId, module)]));
        }

        return
        [
            new KodoMetricFamily(
                KodoMetricNames.State,
                "Kodo's displayed health state: 1 for the active state, 0 for the others.",
                "1",
                state),
            new KodoMetricFamily(
                KodoMetricNames.UnderlyingState,
                "The underlying health state shown beside frozen: 1 for the active state, 0 for the others.",
                "1",
                underlying),
            new KodoMetricFamily(
                KodoMetricNames.Score,
                "Kodo's health score, from 0 to 100.",
                "1",
                [new KodoMetricPoint(snapshot.Status.Score, [])]),
            new KodoMetricFamily(
                KodoMetricNames.ProbeStatus,
                "Per-probe status: 0 green, 1 amber, 2 red, 3 unknown. Unknown never reads as green.",
                "1",
                probes),
        ];
    }

    /// <summary>The probe status value for a wire status. Unknown is the highest value, so an alert on above zero also fires for unknown.</summary>
    private static double Severity(string wireStatus) => wireStatus switch
    {
        "green" => 0,
        "amber" => 1,
        "red" => 2,
        "unknown" => 3,
        _ => throw new ArgumentException($"'{wireStatus}' is not a probe status.", nameof(wireStatus)),
    };

    private static double Flag(bool active) => active ? 1 : 0;

    private static string ValidateComponentId(string id)
    {
        if (id.Length > 128
            || !KodoExportPatterns.ComponentId().IsMatch(id)
            || KodoExportPatterns.InstanceIdLike().IsMatch(id))
        {
            throw new ArgumentException($"'{id}' is not a probe or category id the export can label.", nameof(id));
        }

        return id;
    }

    private static string ValidateModuleId(string? module)
    {
        if (module is null || !KodoExportPatterns.ModuleId().IsMatch(module))
        {
            throw new ArgumentException($"'{module}' is not a module id the export can label.", nameof(module));
        }

        return module;
    }
}
