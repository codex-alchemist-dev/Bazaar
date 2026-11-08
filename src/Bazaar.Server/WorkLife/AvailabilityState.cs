namespace Bazaar.Server.WorkLife;

/// <summary>
/// States the schedule calculator produces. Draining and break belong to the availability machine,
/// which takes this window as input.
/// </summary>
public enum AvailabilityState
{
    /// <summary>Inside a shift and not in curfew or vacation.</summary>
    OnShift,

    /// <summary>Outside every shift, or in curfew.</summary>
    Off,

    /// <summary>Covered by a vacation event.</summary>
    Vacation,
}
