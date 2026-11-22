using Bazaar.Kodo.Responder;

namespace Bazaar.Kodo.Incidents;

/// <summary>Which incident event a notification announces.</summary>
public enum IncidentNotice
{
    Opened,
    Resolved,
    Closed,
}

/// <summary>
/// Sends the incident's notifications through the Responder (doc 46 section 16.3 item 4). It builds one <see cref="KodoAlert"/> and hands it
/// to the existing <see cref="AlertRouter"/> and <see cref="AlertDispatcher"/>, so severity routing, dedupe, rate limits, breakers and
/// escalation all come from the Responder. The content is identifiers only: the incident id is the link, and the title, severity word and
/// phase are fetched over the authenticated connection. No secrets, no content.
/// </summary>
public sealed class IncidentNotifier
{
    /// <summary>The alert category. Routing uses its first segment, <c>kodo</c>.</summary>
    public const string Category = "kodo.incident";

    private readonly AlertRouter _router;
    private readonly AlertDispatcher _dispatcher;
    private readonly TimeProvider _time;

    /// <summary>Creates a notifier over the shared router and dispatcher.</summary>
    public IncidentNotifier(AlertRouter router, AlertDispatcher dispatcher, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _router = router;
        _dispatcher = dispatcher;
        _time = timeProvider;
    }

    /// <summary>The alert for an event. An opening carries the incident's severity; a resolution ends tracking; the close is informational.</summary>
    public KodoAlert Build(IncidentRecord incident, IncidentNotice notice)
    {
        ArgumentNullException.ThrowIfNull(incident);
        (AlertSeverity severity, string rule, bool resolution) = notice switch
        {
            IncidentNotice.Opened => (incident.Severity, "incident_opened", false),
            IncidentNotice.Resolved => (incident.Severity, "incident_resolved", true),
            IncidentNotice.Closed => (AlertSeverity.Info, "incident_closed", false),
            _ => throw new ArgumentOutOfRangeException(nameof(notice), notice, "Not an incident notice."),
        };

        return new KodoAlert(
            $"kodo.incident:{incident.Id}",
            Category,
            severity,
            rule,
            _time.GetUtcNow(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["incident_id"] = incident.Id,
                ["incident_phase"] = incident.Phase.ToString().ToLowerInvariant(),
                ["incident_trigger"] = incident.Trigger.ToString().ToLowerInvariant(),
            },
            resolution);
    }

    /// <summary>Routes the alert and sends what the router allows. A failed channel never throws; the router's answer says what happened.</summary>
    public async ValueTask<AlertRouteResult> NotifyAsync(IncidentRecord incident, IncidentNotice notice, CancellationToken cancellationToken)
    {
        AlertRouteResult routed = _router.Route(Build(incident, notice));
        if (routed.Deliveries.Count > 0)
        {
            await _dispatcher.DispatchAsync(routed.Deliveries, cancellationToken).ConfigureAwait(false);
        }

        return routed;
    }
}
