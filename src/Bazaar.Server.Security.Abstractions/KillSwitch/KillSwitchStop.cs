namespace Bazaar.Server.Security.KillSwitch;

/// <summary>One engaged stop, as the status view reports it.</summary>
/// <param name="Scope">Global or agent.</param>
/// <param name="AgentId">The stopped agent. Null for a global stop.</param>
/// <param name="ActorId">The authorized actor who engaged the stop, or the breach actor for an automatic stop.</param>
/// <param name="Reason">Why the stop was engaged.</param>
/// <param name="Since">When the stop was engaged, from the engine's <see cref="TimeProvider"/>.</param>
public sealed record KillSwitchStop(KillSwitchScope Scope, string? AgentId, string ActorId, string Reason, DateTimeOffset Since);

/// <summary>
/// Read-only snapshot of the kill switch for the status page and the admin panel. Reading it is always allowed, even under
/// a global stop.
/// </summary>
/// <param name="Global">The global stop, or null when egress and writes are open.</param>
/// <param name="AgentStops">Every agent currently stopped, ordered by agent id.</param>
public sealed record KillSwitchStatus(KillSwitchStop? Global, IReadOnlyList<KillSwitchStop> AgentStops);
