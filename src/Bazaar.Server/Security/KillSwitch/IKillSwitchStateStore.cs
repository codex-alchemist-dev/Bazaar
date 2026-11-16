namespace Bazaar.Server.Security.KillSwitch;

/// <summary>
/// Durable home of the kill switch's current stops (migration agent_safety 0003). The engine writes every change here before
/// it changes its in-memory cache, and loads the whole state when it starts, so a restart keeps every engaged stop.
/// </summary>
/// <remarks>
/// Each write replaces the current state for its scope and completes only when the change is durable. A write that throws
/// must leave the stored state as it was. Implementations are safe to call from several tasks; the engine serialises its own
/// calls.
/// </remarks>
public interface IKillSwitchStateStore
{
    /// <summary>Reads the global stop and every agent stop. Returns an empty status when nothing is engaged.</summary>
    Task<KillSwitchStatus> LoadAsync(CancellationToken ct = default);

    /// <summary>Stores the global stop, replacing any stored global stop.</summary>
    Task SetGlobalAsync(KillSwitchStop engaged, CancellationToken ct = default);

    /// <summary>Removes the stored global stop. Does nothing when none is stored.</summary>
    Task ClearGlobalAsync(CancellationToken ct = default);

    /// <summary>Stores an agent stop, replacing any stored stop for the same agent. <see cref="KillSwitchStop.AgentId"/> must be set.</summary>
    Task SetAgentStopAsync(KillSwitchStop engaged, CancellationToken ct = default);

    /// <summary>Removes the stored stop for one agent. Does nothing when none is stored.</summary>
    Task ClearAgentStopAsync(string agentId, CancellationToken ct = default);
}
