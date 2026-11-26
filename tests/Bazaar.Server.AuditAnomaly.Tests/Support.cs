using Bazaar.Server.Security.Audit;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.AuditAnomaly.Tests;

internal sealed class ListAuditLog : IAuditLog
{
    public List<AuditEntry> Entries { get; } = [];

    public Task AppendAsync(AuditEntry e, CancellationToken ct = default)
    {
        Entries.Add(e);
        return Task.CompletedTask;
    }
}

internal sealed class ListSource(List<AnomalyEvent> events) : IAnomalyEventSource
{
    public Task<IReadOnlyList<AnomalyEvent>> ReadAsync(DateTimeOffset after, DateTimeOffset upTo, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AnomalyEvent>>(events.Where(e => e.Ts > after && e.Ts <= upTo).ToList());
}

internal sealed class FakeScope(Func<string, string, string?, bool> inScope) : IAgentScopeProvider
{
    public bool IsInScope(string principal, string objectType, string? objectId) => inScope(principal, objectType, objectId);
}

internal sealed class FakePort : IContainmentPort
{
    public List<(string Op, ContainmentTarget Target, ContainmentActions Actions)> Calls { get; } = [];

    public Task ApplyAsync(ContainmentTarget target, ContainmentActions actions, CancellationToken ct)
    {
        Calls.Add(("apply", target, actions));
        return Task.CompletedTask;
    }

    public Task RestoreAsync(ContainmentTarget target, ContainmentActions actions, CancellationToken ct)
    {
        Calls.Add(("restore", target, actions));
        return Task.CompletedTask;
    }
}

internal sealed class FakeBiometric(bool result, bool throws = false) : IBiometricCheck
{
    public int Calls { get; private set; }

    public Task<bool> VerifyAsync(string principalId, string purpose, CancellationToken ct)
    {
        Calls++;
        return throws ? throw new InvalidOperationException("device") : Task.FromResult(result);
    }
}

internal sealed class ThrowingSink : IAnomalyAlertSink
{
    public Task NotifyAsync(AnomalyAlert alert, AlertAudience audience, CancellationToken ct) => throw new InvalidOperationException("down");
}

internal sealed class Rig
{
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
    public ListAuditLog Audit { get; } = new();
    public List<AnomalyEvent> Events { get; } = [];
    public FakePort Port { get; } = new();
    public InMemoryAlertSink Sink { get; } = new();
    public Func<string, string, string?, bool> InScope { get; set; } = (_, _, _) => true;
    public IAnomalyAlertSink SinkToUse { get; set; }
    public IBiometricCheck Biometric { get; set; } = new FakeBiometric(true);
    public AnomalySettings Settings { get; set; } = new();

    public Rig() => SinkToUse = Sink;

    public ContainmentService Containment() =>
        new(Port, _store, Biometric, Audit, Settings, Clock);

    private readonly InMemoryContainmentStore _store = new();

    public AnomalyEvaluator Evaluator(ContainmentService? c = null) =>
        new(new ListSource(Events), new FakeScope(InScope), SinkToUse, c ?? Containment(), Audit, Settings, Clock);

    public AnomalyEvent Ev(string kind, string principal = "agent-1", TimeSpan? ago = null, string? type = null, string? id = null, long count = 1, string? session = "s1") =>
        new(Clock.GetUtcNow() - (ago ?? TimeSpan.Zero), kind, principal, session, type, id, count);
}
