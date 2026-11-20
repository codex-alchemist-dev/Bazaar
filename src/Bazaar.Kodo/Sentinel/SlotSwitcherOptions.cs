namespace Bazaar.Kodo.Sentinel;

/// <summary>
/// Options for the slot switcher. The probation window is the setting <c>kodo.integrity.probation_min</c> (doc 46 section 20),
/// which defaults to 10 minutes.
/// </summary>
/// <param name="ProbationWindow">How long a promoted slot must survive before it is kept.</param>
public sealed record SlotSwitcherOptions(TimeSpan ProbationWindow)
{
    /// <summary>The shipped options: a 10 minute probation window.</summary>
    public static SlotSwitcherOptions Default { get; } = new(TimeSpan.FromMinutes(10));
}
