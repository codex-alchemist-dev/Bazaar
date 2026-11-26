using Bazaar.Kodo.Export;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Responder;

/// <summary>The identifiers-only body of one delivery (section 15.3). Push wake-ups carry nothing but the alert id.</summary>
/// <param name="EventType">One of <see cref="KodoWebhookEvents.All"/>.</param>
/// <param name="EventId">URL-safe id a consumer deduplicates on.</param>
/// <param name="OccurredAt">When the alert was raised.</param>
/// <param name="Identifiers">Identifier names and values only.</param>
public sealed record AlertPayload(string EventType, string EventId, DateTimeOffset OccurredAt, IReadOnlyDictionary<string, string> Identifiers)
{
    /// <summary>
    /// The signed-webhook envelope for this payload. It goes through <see cref="KodoWebhookEnvelopeBuilder"/>, so one encoding rule
    /// and one identifier check serve every channel; signing and retry stay in the export code.
    /// </summary>
    public KodoWebhookEnvelope ToWebhookEnvelope() => KodoWebhookEnvelopeBuilder.Build(EventType, EventId, OccurredAt, Identifiers);
}

/// <summary>One thing the router wants sent. The caller hands it to the matching <see cref="IAlertChannel"/>.</summary>
/// <param name="DeliveryId">A unique id for this delivery.</param>
/// <param name="AlertId">The alert's id.</param>
/// <param name="Channel">The channel.</param>
/// <param name="Target">The role the delivery is for, such as <c>owner</c>, or <c>owner-devices</c> after escalation.</param>
/// <param name="Severity">The alert's severity.</param>
/// <param name="Payload">The identifiers-only payload.</param>
/// <param name="Ticket">The breaker ticket, to hand back to <see cref="AlertRouter.RecordResult"/>.</param>
/// <param name="EscalationLevel">0 for the first delivery; 1 and up for escalations.</param>
public sealed record AlertDelivery(
    string DeliveryId,
    string AlertId,
    AlertChannelKind Channel,
    string Target,
    AlertSeverity Severity,
    AlertPayload Payload,
    BreakerTicket Ticket,
    int EscalationLevel);

/// <summary>A channel the router chose not to use, with a fixed reason code.</summary>
/// <param name="Channel">The channel.</param>
/// <param name="Reason">One of <c>quiet</c>, <c>rate_limited</c> or <c>breaker_open</c>.</param>
public sealed record AlertSuppression(AlertChannelKind Channel, string Reason);

/// <summary>What the router did with one alert.</summary>
public enum AlertDecision
{
    /// <summary>At least one delivery was produced.</summary>
    Delivered,

    /// <summary>Nothing is to be sent. See the reason.</summary>
    Suppressed,

    /// <summary>The alert was not acceptable. Nothing is sent, nothing is tracked.</summary>
    Rejected,
}

/// <summary>The router's answer for one alert.</summary>
/// <param name="Decision">What happened.</param>
/// <param name="Reason">A fixed code: <c>delivered</c>, <c>duplicate</c>, <c>grouped</c>, <c>nothing_to_resolve</c>, a suppression reason, or an alert problem code.</param>
/// <param name="AlertId">The alert's id, or null when rejected.</param>
/// <param name="Deliveries">What to send.</param>
/// <param name="Suppressions">Channels that were skipped, for the journal.</param>
public sealed record AlertRouteResult(
    AlertDecision Decision,
    string Reason,
    string? AlertId,
    IReadOnlyList<AlertDelivery> Deliveries,
    IReadOnlyList<AlertSuppression> Suppressions);

/// <summary>An unacknowledged critical alert that moved up its role chain (section 16.1). The caller journals it.</summary>
/// <param name="AlertId">The alert's id.</param>
/// <param name="Fingerprint">The alert's fingerprint.</param>
/// <param name="FromTarget">The role that did not acknowledge.</param>
/// <param name="ToTarget">The next role.</param>
/// <param name="Deliveries">What to send to the next role.</param>
/// <param name="Suppressions">Channels that were skipped.</param>
public sealed record AlertEscalation(
    string AlertId,
    string Fingerprint,
    string FromTarget,
    string ToTarget,
    IReadOnlyList<AlertDelivery> Deliveries,
    IReadOnlyList<AlertSuppression> Suppressions);

/// <summary>The outcome of one send, as the channel reports it.</summary>
public enum AlertSendOutcome
{
    Sent,
    Failed,
}

/// <summary>
/// One alert channel behind an interface (section 16.1): in-client, push provider, email, webhook, GC post, report. The implementation does
/// One alert channel behind an interface (section 16.1): in-client, push provider, email, webhook, group-chat post, report. The implementation does
/// the I/O. It never throws for a delivery failure; it returns <see cref="AlertSendOutcome.Failed"/>.
/// </summary>
public interface IAlertChannel
{
    /// <summary>The kind this channel serves.</summary>
    AlertChannelKind Kind { get; }

    /// <summary>Sends one delivery.</summary>
    ValueTask<AlertSendOutcome> SendAsync(AlertDelivery delivery, CancellationToken cancellationToken);
}

/// <summary>Builds the identifiers-only payloads. Push carries nothing but the alert id; email carries severity, rule and id.</summary>
public static class AlertPayloads
{
    /// <summary>Names the router writes itself, so an alert may not carry them.</summary>
    public static IReadOnlySet<string> ReservedNames { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "alert_id", "severity", "category", "rule", "fingerprint", "resolved", "grouped_count", "escalation" };

    /// <summary>The wire word of a severity.</summary>
    public static string Word(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Info => "info",
        AlertSeverity.Warning => "warning",
        AlertSeverity.Critical => "critical",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, "Not an alert severity."),
    };

    /// <summary>The payload one channel gets.</summary>
    public static AlertPayload For(AlertChannelKind channel, KodoAlert alert, string alertId, int level)
    {
        string eventId = level == 0 ? alertId : $"{alertId}-e{level}";
        Dictionary<string, string> ids = new(StringComparer.Ordinal) { ["alert_id"] = alertId };

        if (channel != AlertChannelKind.Push)
        {
            ids["severity"] = Word(alert.Severity);
            ids["rule"] = alert.Rule;
        }

        if (channel is not (AlertChannelKind.Push or AlertChannelKind.Email))
        {
            ids["category"] = alert.Category;
            ids["fingerprint"] = alert.Fingerprint;
            foreach (KeyValuePair<string, string> pair in alert.Identifiers)
            {
                ids[pair.Key] = pair.Value;
            }

            if (alert.IsResolution)
            {
                ids["resolved"] = "true";
            }

            if (level > 0)
            {
                ids["escalation"] = level.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return new AlertPayload(KodoWebhookEvents.Alert, eventId, alert.RaisedAt, ids);
    }
}

/// <summary>
/// Routes alerts (doc 46 sections 13.4 and 16.1) into deliveries. It deduplicates by fingerprint, groups a category's burst, applies the
/// quiet window and a per-channel rate limit and breaker, and escalates unacknowledged critical alerts along the owning role's chain.
/// It does no I/O and reads no clock except the <see cref="TimeProvider"/>. It is not thread-safe: one caller at a time.
/// </summary>
public sealed class AlertRouter
{
    /// <summary>The role every chain ends at before the owner's devices.</summary>
    public const string OwnerRole = "owner";

    /// <summary>The last link in every chain (section 16.1: "then to the owner's devices").</summary>
    public const string OwnerDevices = "owner-devices";

    private readonly AlertOptions _options;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, SeenAlert> _seen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
    private readonly List<KodoAlert> _expiredSummaries = [];
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<AlertChannelKind, CircuitBreaker> _breakers = [];
    private readonly Dictionary<(AlertChannelKind, bool), Queue<DateTimeOffset>> _rates = [];
    private long _sequence;

    /// <summary>Creates a router. The options are validated.</summary>
    public AlertRouter(AlertOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _options = options;
        _time = timeProvider;
    }

    /// <summary>Critical alerts still waiting for an acknowledgement.</summary>
    public int PendingAcknowledgements => _pending.Count;

    /// <summary>Routes one alert.</summary>
    public AlertRouteResult Route(KodoAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        string? problem = alert.Problem();
        if (problem is not null)
        {
            return new AlertRouteResult(AlertDecision.Rejected, problem, null, [], []);
        }

        DateTimeOffset now = _time.GetUtcNow();
        Prune(now);

        if (alert.IsResolution)
        {
            return Resolve(alert, now);
        }

        if (_seen.TryGetValue(alert.Fingerprint, out SeenAlert? seen)
            && alert.Severity == seen.Severity
            && now - seen.At < _options.DedupeWindow)
        {
            seen.Suppressed++;
            return Suppressed("duplicate", null);
        }

        if (_options.GroupWindow > TimeSpan.Zero)
        {
            if (_groups.TryGetValue(alert.Category, out Group? group) && now - group.FirstAt < _options.GroupWindow)
            {
                group.Count++;
                if (alert.Severity <= group.Max)
                {
                    return Suppressed("grouped", null);
                }

                group.Max = alert.Severity;
            }
            else if (_groups.Count < _options.MaxTracked || group is not null)
            {
                _groups[alert.Category] = new Group(now, alert.Severity);
            }
        }

        return Deliver(alert, now, applyQuiet: true);
    }

    /// <summary>
    /// Emits one summary alert for each category whose group window has ended with more than one alert in it (section 8.2: one grouped
    /// alert, not a stream). Call it on a timer.
    /// </summary>
    public IReadOnlyList<AlertRouteResult> FlushGroups()
    {
        DateTimeOffset now = _time.GetUtcNow();
        CollectExpiredGroups(now);

        List<AlertRouteResult> results = [];
        foreach (KodoAlert summary in _expiredSummaries)
        {
            results.Add(Deliver(summary, now, applyQuiet: true));
        }

        _expiredSummaries.Clear();
        return results;
    }

    /// <summary>An owner or role acknowledged the alert. Returns false when no such alert was waiting.</summary>
    public bool Acknowledge(string fingerprint) => _pending.Remove(fingerprint);

    /// <summary>
    /// Moves every critical alert that has waited past the escalation window to the next role in its chain (section 16.1). The caller
    /// sends the deliveries and journals each escalation. An alert at the end of its chain stays pending and is not escalated again.
    /// </summary>
    public IReadOnlyList<AlertEscalation> Escalate()
    {
        DateTimeOffset now = _time.GetUtcNow();
        List<AlertEscalation> escalations = [];

        foreach (Pending pending in _pending.Values.OrderBy(entry => entry.AlertId, StringComparer.Ordinal))
        {
            if (now - pending.LastAt < _options.CriticalEscalation || pending.Level + 1 >= pending.Chain.Count)
            {
                continue;
            }

            string from = pending.Chain[pending.Level];
            pending.Level++;
            pending.LastAt = now;
            string to = pending.Chain[pending.Level];

            (List<AlertDelivery> deliveries, List<AlertSuppression> suppressions) =
                Fan(pending.Alert, pending.AlertId, to, pending.Level, now, quiet: false);
            escalations.Add(new AlertEscalation(pending.AlertId, pending.Alert.Fingerprint, from, to, deliveries, suppressions));
        }

        return escalations;
    }

    /// <summary>Tells the router how a send went, so the channel's breaker can open or close.</summary>
    public void RecordResult(AlertDelivery delivery, bool failed)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (_breakers.TryGetValue(delivery.Channel, out CircuitBreaker? breaker))
        {
            breaker.Record(delivery.Ticket, failed);
        }
    }

    /// <summary>Frees a half-open trial whose send never completed (the caller cancelled). It is not counted as a failure.</summary>
    public void ReleaseResult(AlertDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (_breakers.TryGetValue(delivery.Channel, out CircuitBreaker? breaker))
        {
            breaker.ReleaseTrial(delivery.Ticket);
        }
    }

    /// <summary>The breaker state of a channel. A channel never used reads as closed.</summary>
    public BreakerState BreakerStateOf(AlertChannelKind channel) =>
        _breakers.TryGetValue(channel, out CircuitBreaker? breaker) ? breaker.State : BreakerState.Closed;

    private AlertRouteResult Resolve(KodoAlert alert, DateTimeOffset now)
    {
        bool wasSeen = _seen.Remove(alert.Fingerprint);
        bool wasPending = _pending.Remove(alert.Fingerprint);
        if (!wasSeen && !wasPending)
        {
            return Suppressed("nothing_to_resolve", null);
        }

        return Deliver(alert, now, applyQuiet: true);
    }

    private AlertRouteResult Deliver(KodoAlert alert, DateTimeOffset now, bool applyQuiet)
    {
        string alertId = $"a{++_sequence}";
        string target = Chain(alert.Category)[0];
        bool quiet = applyQuiet && alert.Severity < AlertSeverity.Critical && _options.Quiet?.Contains(now) == true;
        (List<AlertDelivery> deliveries, List<AlertSuppression> suppressions) = Fan(alert, alertId, target, 0, now, quiet);

        if (deliveries.Count == 0)
        {
            return new AlertRouteResult(AlertDecision.Suppressed, suppressions.Count > 0 ? suppressions[0].Reason : "no_channels", alertId, [], suppressions);
        }

        if (!alert.IsResolution)
        {
            if (_seen.Count < _options.MaxTracked || _seen.ContainsKey(alert.Fingerprint))
            {
                _seen[alert.Fingerprint] = new SeenAlert(now, alert.Severity);
            }

            if (alert.Severity == AlertSeverity.Critical && (_pending.Count < _options.MaxTracked || _pending.ContainsKey(alert.Fingerprint)))
            {
                _pending[alert.Fingerprint] = new Pending(alert, alertId, Chain(alert.Category), now);
            }
        }

        return new AlertRouteResult(AlertDecision.Delivered, "delivered", alertId, deliveries, suppressions);
    }

    private (List<AlertDelivery> Deliveries, List<AlertSuppression> Suppressions) Fan(
        KodoAlert alert, string alertId, string target, int level, DateTimeOffset now, bool quiet)
    {
        List<AlertDelivery> deliveries = [];
        List<AlertSuppression> suppressions = [];
        bool critical = alert.Severity == AlertSeverity.Critical;

        foreach (AlertChannelKind channel in _options.Channels[alert.Severity])
        {
            if (quiet && channel is not (AlertChannelKind.InClient or AlertChannelKind.Report))
            {
                suppressions.Add(new AlertSuppression(channel, "quiet"));
                continue;
            }

            Queue<DateTimeOffset> window = RateQueue(channel, critical);
            while (window.Count > 0 && now - window.Peek() >= _options.RateWindow)
            {
                window.Dequeue();
            }

            if (window.Count >= (critical ? _options.MaxCriticalPerChannel : _options.MaxPerChannel))
            {
                suppressions.Add(new AlertSuppression(channel, "rate_limited"));
                continue;
            }

            if (!Breaker(channel).TryEnter(out BreakerTicket ticket))
            {
                suppressions.Add(new AlertSuppression(channel, "breaker_open"));
                continue;
            }

            window.Enqueue(now);
            AlertPayload payload = AlertPayloads.For(channel, alert, alertId, level);
            deliveries.Add(new AlertDelivery($"d{++_sequence}", alertId, channel, target, alert.Severity, payload, ticket, level));
        }

        return (deliveries, suppressions);
    }

    private IReadOnlyList<string> Chain(string category)
    {
        int dot = category.IndexOf('.', StringComparison.Ordinal);
        string head = dot < 0 ? category : category[..dot];
        string owner = _options.CategoryOwners.TryGetValue(head, out string? role) && !string.IsNullOrWhiteSpace(role) ? role : OwnerRole;
        return owner is OwnerRole ? [OwnerRole, OwnerDevices] : [owner, OwnerRole, OwnerDevices];
    }

    private CircuitBreaker Breaker(AlertChannelKind channel)
    {
        if (!_breakers.TryGetValue(channel, out CircuitBreaker? breaker))
        {
            breaker = new CircuitBreaker(_options.BreakerFailures, _options.BreakerCooldown, _time);
            _breakers[channel] = breaker;
        }

        return breaker;
    }

    private Queue<DateTimeOffset> RateQueue(AlertChannelKind channel, bool critical)
    {
        if (!_rates.TryGetValue((channel, critical), out Queue<DateTimeOffset>? queue))
        {
            queue = new Queue<DateTimeOffset>();
            _rates[(channel, critical)] = queue;
        }

        return queue;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (string key in _seen.Where(entry => now - entry.Value.At >= _options.DedupeWindow).Select(entry => entry.Key).ToList())
        {
            _seen.Remove(key);
        }

        CollectExpiredGroups(now);
    }

    private void CollectExpiredGroups(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, Group> entry in _groups.Where(g => now - g.Value.FirstAt >= _options.GroupWindow).ToList())
        {
            _groups.Remove(entry.Key);
            if (entry.Value.Count > 1)
            {
                _expiredSummaries.Add(new KodoAlert(
                    $"group:{entry.Key}",
                    entry.Key,
                    entry.Value.Max,
                    "grouped_alerts",
                    now,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["count"] = entry.Value.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    }));
            }
        }
    }

    private static AlertRouteResult Suppressed(string reason, string? alertId) =>
        new(AlertDecision.Suppressed, reason, alertId, [], []);

    private sealed class SeenAlert(DateTimeOffset at, AlertSeverity severity)
    {
        public DateTimeOffset At { get; } = at;

        public AlertSeverity Severity { get; } = severity;

        public int Suppressed { get; set; }
    }

    private sealed class Group(DateTimeOffset firstAt, AlertSeverity max)
    {
        public DateTimeOffset FirstAt { get; } = firstAt;

        public AlertSeverity Max { get; set; } = max;

        public int Count { get; set; } = 1;
    }

    private sealed class Pending(KodoAlert alert, string alertId, IReadOnlyList<string> chain, DateTimeOffset at)
    {
        public KodoAlert Alert { get; } = alert;

        public string AlertId { get; } = alertId;

        public IReadOnlyList<string> Chain { get; } = chain;

        public int Level { get; set; }

        public DateTimeOffset LastAt { get; set; } = at;
    }
}

/// <summary>
/// Sends the router's deliveries through <see cref="IAlertChannel"/> implementations and tells the router the results, so breakers work.
/// A missing channel or a thrown exception counts as a failed send. Retry planning for webhooks stays in <see cref="KodoWebhookRetryPolicy"/>.
/// </summary>
public sealed class AlertDispatcher
{
    private readonly Dictionary<AlertChannelKind, IAlertChannel> _channels;
    private readonly AlertRouter _router;

    /// <summary>Creates a dispatcher over the given channels.</summary>
    public AlertDispatcher(AlertRouter router, IEnumerable<IAlertChannel> channels)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(channels);
        _router = router;
        _channels = channels.ToDictionary(channel => channel.Kind);
    }

    /// <summary>Sends each delivery once and returns the outcomes in order.</summary>
    public async ValueTask<IReadOnlyList<(AlertDelivery Delivery, AlertSendOutcome Outcome)>> DispatchAsync(
        IEnumerable<AlertDelivery> deliveries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deliveries);
        List<(AlertDelivery, AlertSendOutcome)> outcomes = [];

        foreach (AlertDelivery delivery in deliveries)
        {
            AlertSendOutcome outcome = AlertSendOutcome.Failed;
            if (_channels.TryGetValue(delivery.Channel, out IAlertChannel? channel))
            {
                try
                {
                    outcome = await channel.SendAsync(delivery, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _router.ReleaseResult(delivery);
                    throw;
                }
                catch (Exception)
                {
                    outcome = AlertSendOutcome.Failed;
                }
            }

            _router.RecordResult(delivery, failed: outcome != AlertSendOutcome.Sent);
            outcomes.Add((delivery, outcome));
        }

        return outcomes;
    }
}
