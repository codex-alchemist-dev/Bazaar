namespace Bazaar.Server.Interrupts;

/// <summary>
/// Decides which interrupts may wake an agent during a vacation (doc 24 section 3, DECIDED C35).
/// Only an owner interrupt tagged <c>emergency</c> bypasses vacation. Untagged owner pushes wait, and system pushes
/// follow the schedule. The curfew and break bypasses belong to doc 09 settings and are not decided here.
/// </summary>
public static class InterruptWakePolicy
{
    /// <summary>True when the push may wake an agent that is on vacation.</summary>
    /// <param name="source">Who pushed the interrupt.</param>
    /// <param name="emergencyTag">Whether the owner tagged the push <c>emergency</c>.</param>
    public static bool BypassesVacation(InterruptSource source, bool emergencyTag) =>
        source == InterruptSource.Owner && emergencyTag;
}
