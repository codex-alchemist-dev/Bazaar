namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// Read side of the running-turn registry (doc 06, "Running-turn registry"). A miss means "not a running turn": callers reject,
/// they never default.
/// </summary>
public interface IRunningTurnRegistry
{
    /// <summary>Returns the running turn, or null when no turn with that id holds a lease.</summary>
    RunningTurnInfo? TryGet(Guid turnId);
}

/// <summary>
/// Write side, for the turn runner only. A turn is registered once its lease is held and the registration is disposed when the
/// turn ends for any reason, before the lease is released, so an entry never outlives its lease.
/// </summary>
public interface IRunningTurnRegistrar
{
    /// <summary>Adds the turn. Disposing the result removes it; disposing twice is harmless.</summary>
    /// <exception cref="InvalidOperationException">A turn with the same id is already registered.</exception>
    IDisposable Register(RunningTurnInfo info);
}
