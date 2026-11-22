using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AuditAnomaly;

/// <summary>
/// Windowed, deterministic rule evaluation (doc 48 section 5.6). One <see cref="EvaluateAsync"/> pass reads the events of the
/// longest rule window from the source, counts per agent over each rule's window ending at the injected clock's now, and for
/// each rule that reaches its threshold writes an audit row, applies the rule's containment (if auto-containment is on) and
/// notifies owner devices and security reviewers. A rule does not fire again for the same agent until its window has passed.
/// No model scoring.
/// </summary>
public sealed class AnomalyEvaluator(
    IAnomalyEventSource source, IAgentScopeProvider scope, IAnomalyAlertSink sink, ContainmentService containment, IAuditLog audit,
    AnomalySettings settings, TimeProvider clock)
{
    private readonly Dictionary<(string Rule, string Principal), DateTimeOffset> _lastFired = [];

    public async Task<IReadOnlyList<AnomalyAlert>> EvaluateAsync(CancellationToken ct = default)
    {
        var problems = settings.Validate();
        if (problems.Count > 0)
            throw new InvalidOperationException("Anomaly settings are invalid: " + string.Join(" ", problems));
        var rules = settings.Rules.Where(r => r.Enabled).ToList();
        if (rules.Count == 0)
            return [];

        var now = clock.GetUtcNow();
        var events = await source.ReadAsync(now - rules.Max(r => r.Window), now, ct).ConfigureAwait(false);
        var alerts = new List<AnomalyAlert>();
        foreach (var rule in rules)
        {
            var from = now - rule.Window;
            var inWindow = events.Where(e => e.Ts > from && e.Ts <= now);
            foreach (var group in inWindow.GroupBy(e => e.Principal, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var matching = group.Where(e => Matches(rule, e)).ToList();
                var observed = matching.Sum(e => e.Count);
                if (matching.Count == 0 || observed < rule.Threshold)
                    continue;
                if (_lastFired.TryGetValue((rule.Id, group.Key), out var last) && now - last < rule.Window)
                    continue;
                _lastFired[(rule.Id, group.Key)] = now;
                var session = matching.OrderByDescending(e => e.Ts).Select(e => e.Session).FirstOrDefault(s => s is not null);
                alerts.Add(await FireAsync(rule, group.Key, session, observed, now, ct).ConfigureAwait(false));
            }
        }
        return alerts;
    }

    private bool Matches(AnomalyRule rule, AnomalyEvent e) => rule.Kind switch
    {
        AnomalyRuleKind.ReadBurst => e.Kind == AnomalyEventKinds.DataRead && e.ObjectType == rule.ObjectClass,
        AnomalyRuleKind.ReadOutsideScope => e.Kind == AnomalyEventKinds.DataRead && e.ObjectType is not null
            && !scope.IsInScope(e.Principal, e.ObjectType, e.ObjectId),
        AnomalyRuleKind.CanaryHit => e.Kind == AnomalyEventKinds.CanaryHit,
        AnomalyRuleKind.ApprovalFlood => e.Kind == AnomalyEventKinds.ApprovalRequested,
        _ => false,
    };

    private async Task<AnomalyAlert> FireAsync(AnomalyRule rule, string principal, string? session, long observed, DateTimeOffset now, CancellationToken ct)
    {
        Guid? containmentId = null;
        string? containmentOutcome = null;
        if (settings.AutoContainmentEnabled && rule.Actions != ContainmentActions.None)
        {
            var target = rule.AutoScope == ContainmentScope.Session && session is not null
                ? new ContainmentTarget(ContainmentScope.Session, session)
                : new ContainmentTarget(ContainmentScope.Agent, principal);
            var result = await containment.ContainAsync(target, rule.Actions, $"rule {rule.Id}",
                new Requester(AnomalyAudit.Engine, IsOwner: false), ct).ConfigureAwait(false);
            containmentId = result.Record?.Id;
            containmentOutcome = result.Outcome.ToString();
        }

        var alert = new AnomalyAlert(Guid.NewGuid(), now, rule.Id, rule.Kind, rule.Severity, principal, session, observed,
            rule.Threshold, rule.Window, containmentId);
        var payload = new JsonObject
        {
            ["alert_id"] = alert.Id.ToString(),
            ["rule"] = rule.Id,
            ["kind"] = rule.Kind.ToString(),
            ["severity"] = rule.Severity.ToString(),
            ["principal"] = principal,
            ["session"] = session,
            ["observed"] = observed,
            ["threshold"] = rule.Threshold,
            ["window_seconds"] = rule.Window.TotalSeconds,
            ["containment_id"] = containmentId?.ToString(),
            ["containment_outcome"] = containmentOutcome,
        };
        await AnomalyAudit.WriteAsync(audit, clock, AnomalyAudit.Engine, AnomalyAuditKinds.Alert, payload, ct).ConfigureAwait(false);

        try
        {
            await sink.NotifyAsync(alert, AlertAudience.OwnerDevices | AlertAudience.SecurityReviewers, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AnomalyAudit.WriteAsync(audit, clock, AnomalyAudit.Engine, AnomalyAuditKinds.AlertDeliveryFailed,
                new JsonObject { ["alert_id"] = alert.Id.ToString(), ["error"] = ex.GetType().Name }, ct).ConfigureAwait(false);
        }
        return alert;
    }
}
