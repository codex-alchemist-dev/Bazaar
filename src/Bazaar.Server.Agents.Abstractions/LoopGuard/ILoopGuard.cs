namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>
/// The loop-guard facade (doc 06, "Loop guards"). The runtime reports each event here; a trip means the agents involved
/// go to the Psych Ward (C34) and the caller posts the system notice and writes <c>guard_event</c>.
/// </summary>
public interface ILoopGuard
{
    /// <summary>Records one event and returns the verdict. A trip is returned, never thrown.</summary>
    GuardVerdict Check(LoopEvent loopEvent);

    /// <summary>
    /// Whether a scored wake (doc 13) may still be made for this agent this hour. Reads only; it records nothing and never trips.
    /// </summary>
    bool ScoredWakeWithinCap(string agentId);
}
