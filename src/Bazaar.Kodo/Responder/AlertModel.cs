using Bazaar.Kodo.Api;
using Bazaar.Kodo.Evaluator;
using Bazaar.Kodo.Export;

namespace Bazaar.Kodo.Responder;

/// <summary>Alert severity. It orders: <see cref="Critical"/> is the highest.</summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>The alert channels of doc 46 section 16.1.</summary>
public enum AlertChannelKind
{
    /// <summary>The in-client alert list (every client).</summary>
    InClient,

    /// <summary>A content-free push wake-up (doc 29's push provider interface).</summary>
    Push,

    /// <summary>Outbound email to the owner's external address (external only, doc 22).</summary>
    Email,

    /// <summary>A signed webhook (section 15.3).</summary>
    Webhook,

    /// <summary>An optional post in a GC the owner chooses.</summary>
    GcPost,

    /// <summary>The scheduled health report (16.7).</summary>
    Report,
}

/// <summary>
/// One alert as the evaluator, predictor or sentinel raises it. It is identifiers only (section 15.3): free text and content never enter
/// an alert, so a delivery cannot leak them.
/// </summary>
/// <param name="Fingerprint">Probe id plus scope (13.4), for example <c>host.disk.free:data</c>. Identifier characters only, up to 128.</param>
/// <param name="Category">The category id, such as <c>host</c> or <c>kodo.state</c>. Routing uses its first segment.</param>
/// <param name="Severity">The severity.</param>
/// <param name="Rule">The rule that fired, as a component id. The reason is always visible (13.4).</param>
/// <param name="RaisedAt">When the alert was raised.</param>
/// <param name="Identifiers">Extra identifiers a consumer may use to call the read API. At most <see cref="MaxIdentifiers"/>.</param>
/// <param name="IsResolution">True when the condition cleared. A resolution is delivered once and ends the alert's tracking.</param>
public sealed record KodoAlert(
    string Fingerprint,
    string Category,
    AlertSeverity Severity,
    string Rule,
    DateTimeOffset RaisedAt,
    IReadOnlyDictionary<string, string> Identifiers,
    bool IsResolution = false)
{
    /// <summary>The most extra identifiers an alert may carry. A webhook body carries 16, and the router adds the rest.</summary>
    public const int MaxIdentifiers = 8;

    /// <summary>Why this alert is not acceptable, or null when it is. Failing closed: an unacceptable alert is rejected, never trimmed.</summary>
    public string? Problem()
    {
        if (string.IsNullOrEmpty(Fingerprint) || !KodoExportPatterns.IdentifierValue().IsMatch(Fingerprint))
        {
            return "invalid_fingerprint";
        }

        if (string.IsNullOrEmpty(Category) || !KodoExportPatterns.ComponentId().IsMatch(Category))
        {
            return "invalid_category";
        }

        if (string.IsNullOrEmpty(Rule) || !KodoExportPatterns.ComponentId().IsMatch(Rule))
        {
            return "invalid_rule";
        }

        if (!Enum.IsDefined(Severity))
        {
            return "invalid_severity";
        }

        if (Identifiers is null || Identifiers.Count > MaxIdentifiers)
        {
            return "invalid_identifiers";
        }

        foreach (KeyValuePair<string, string> pair in Identifiers)
        {
            if (pair.Key is null || pair.Value is null
                || !KodoExportPatterns.IdentifierName().IsMatch(pair.Key)
                || !KodoExportPatterns.IdentifierValue().IsMatch(pair.Value)
                || AlertPayloads.ReservedNames.Contains(pair.Key))
            {
                return "invalid_identifiers";
            }
        }

        return null;
    }
}

/// <summary>Builds alerts from the evaluator's output.</summary>
public static class KodoAlerts
{
    /// <summary>The fingerprint of the single grouped flap alert (section 8.2).</summary>
    public const string FlapFingerprint = "kodo.state.flap";

    /// <summary>The fingerprint of the state-change alert.</summary>
    public const string StateFingerprint = "kodo.state.change";

    /// <summary>Builds a fingerprint from a probe id and a scope (13.4).</summary>
    public static string Fingerprint(string probeId, string scope)
    {
        ArgumentException.ThrowIfNullOrEmpty(probeId);
        ArgumentException.ThrowIfNullOrEmpty(scope);
        return $"{probeId}:{scope}";
    }

    /// <summary>
    /// The severity of a state transition (doc 46 section 16.8, PROPOSED), taken from the state moved into: failing and frozen are critical,
    /// degraded is a warning, healthy and recovering are informational. No change returns null, because no alert is due.
    /// </summary>
    public static AlertSeverity? SeverityForTransition(KodoState from, KodoState to)
    {
        if (from == to)
        {
            return null;
        }

        return to switch
        {
            KodoState.Failing or KodoState.Frozen => AlertSeverity.Critical,
            KodoState.Degraded => AlertSeverity.Warning,
            KodoState.Healthy or KodoState.Recovering => AlertSeverity.Info,
            _ => throw new ArgumentOutOfRangeException(nameof(to), to, "Not a Kodo health state."),
        };
    }

    /// <summary>
    /// The alert for a displayed-state change, or null when the state did not change. Moving into healthy is a resolution.
    /// </summary>
    public static KodoAlert? FromStateChange(KodoState from, KodoState to, DateTimeOffset at)
    {
        AlertSeverity? severity = SeverityForTransition(from, to);
        if (severity is null)
        {
            return null;
        }

        return new KodoAlert(
            StateFingerprint,
            "kodo.state",
            severity.Value,
            "state_changed",
            at,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["from_state"] = KodoWireNames.State(from),
                ["to_state"] = KodoWireNames.State(to),
            },
            IsResolution: to == KodoState.Healthy);
    }

    /// <summary>The single grouped alert when the evaluator reports <see cref="KodoEvaluation.RaiseFlapAlert"/> (section 8.2).</summary>
    public static KodoAlert? FromEvaluation(KodoEvaluation evaluation, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!evaluation.RaiseFlapAlert)
        {
            return null;
        }

        return new KodoAlert(
            FlapFingerprint,
            "kodo.state",
            AlertSeverity.Warning,
            "flap_detected",
            at,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
