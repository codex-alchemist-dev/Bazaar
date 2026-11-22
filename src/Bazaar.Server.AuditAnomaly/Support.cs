using System.Text.Json.Nodes;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.AuditAnomaly;

/// <summary>Audit kinds this module writes.</summary>
public static class AnomalyAuditKinds
{
    public const string Alert = "anomaly.alert";
    public const string AlertDeliveryFailed = "anomaly.alert_delivery_failed";
    public const string ContainmentApplied = "containment.applied";
    public const string ContainmentRefused = "containment.refused";
    public const string ContainmentFailed = "containment.failed";
    public const string UndoApplied = "containment.undo";
    public const string UndoRefused = "containment.undo_refused";
}

internal static class AnomalyAudit
{
    public const string Engine = "anomaly-engine";

    public static Task WriteAsync(IAuditLog log, TimeProvider clock, string actor, string kind, JsonObject payload, CancellationToken ct)
    {
        var sorted = new JsonObject();
        foreach (var k in payload.Select(kv => kv.Key).Order(StringComparer.Ordinal))
            sorted[k] = payload[k]?.DeepClone();
        return log.AppendAsync(new AuditEntry(clock.GetUtcNow(), actor, kind, sorted.ToJsonString()), ct);
    }

    public static JsonObject Describe(ContainmentTarget t, ContainmentActions a) => new()
    {
        ["scope"] = t.Scope.ToString().ToLowerInvariant(),
        ["target"] = t.Id,
        ["actions"] = a.ToString(),
    };
}

/// <summary>In-memory containment store.</summary>
public sealed class InMemoryContainmentStore : IContainmentStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ContainmentRecord> _items = new();

    public Task SaveAsync(ContainmentRecord record, CancellationToken ct)
    {
        _items[record.Id] = record;
        return Task.CompletedTask;
    }

    public Task<ContainmentRecord?> FindAsync(Guid id, CancellationToken ct) =>
        Task.FromResult(_items.TryGetValue(id, out var r) ? r : null);
}

/// <summary>The default biometric check: it refuses. A real check replaces it; absent one, undo of a live containment is impossible.</summary>
public sealed class FailClosedBiometricCheck : IBiometricCheck
{
    public Task<bool> VerifyAsync(string principalId, string purpose, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Collects alerts in memory. The only sink that ships; a real notifier is a separate package.</summary>
public sealed class InMemoryAlertSink : IAnomalyAlertSink
{
    private readonly List<(AnomalyAlert Alert, AlertAudience Audience)> _sent = [];

    public IReadOnlyList<(AnomalyAlert Alert, AlertAudience Audience)> Sent
    {
        get { lock (_sent) return [.. _sent]; }
    }

    public Task NotifyAsync(AnomalyAlert alert, AlertAudience audience, CancellationToken ct)
    {
        lock (_sent) _sent.Add((alert, audience));
        return Task.CompletedTask;
    }
}
