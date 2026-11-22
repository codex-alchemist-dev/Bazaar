namespace Bazaar.LoopGuard.Abstractions;

/// <summary>
/// One loop-guard trip on an agent post (docs/design/06 "Loop guards", C34). The agent runtime reports it; the trip handler
/// logs it, tells the GC and admits the agents to the Psych Ward.
/// </summary>
/// <param name="GcId">The GC the refused post was for.</param>
/// <param name="Kind">The guard that tripped, as a lowercase snake_case name (<c>ping_pong</c>, <c>mention_depth</c>, ...). Written to <c>guard_event.kind</c>.</param>
/// <param name="Reason">The guard's reason. Short, no secrets.</param>
/// <param name="AgentIds">The agents involved, distinct and sorted. They are admitted to the Psych Ward.</param>
public sealed record LoopTrip(Guid GcId, string Kind, string Reason, IReadOnlyList<Guid> AgentIds);

/// <summary>
/// Handles a trip: <c>guard_event</c> row, a system notice in the GC, then the Psych Ward admission. The post that tripped is
/// never appended; the caller still refuses it.
/// </summary>
public interface ILoopTripHandler
{
    /// <summary>Records the trip. Safe to call again for the same trip: the notice is idempotent and the audit row is the only repeat.</summary>
    Task HandleAsync(LoopTrip trip, CancellationToken cancellationToken);
}

/// <summary>
/// The Psych Ward hand-off (doc 26, C34). The Psych Ward is not built on the server yet, so the module's default admits
/// nobody; the trip is still recorded and announced. A real Psych Ward replaces this registration.
/// </summary>
public interface IPsychWardGate
{
    /// <summary>Admits the agents in <paramref name="trip"/>. Called once per handled trip, after the guard_event row is written.</summary>
    Task AdmitAsync(LoopTrip trip, CancellationToken cancellationToken);
}
