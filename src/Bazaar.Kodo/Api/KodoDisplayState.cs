namespace Bazaar.Kodo.Api;

/// <summary>
/// What a client shows for Kodo (doc 46 section 8.1 and 8.3): the five server states plus the client-derived
/// <see cref="Unobserved"/>. The server never produces <see cref="Unobserved"/>, so it is not a <c>KodoState</c>.
/// </summary>
public enum KodoDisplayState
{
    Healthy,
    Degraded,
    Failing,
    Recovering,
    Frozen,

    /// <summary>Kodo has been silent past its budget, or no snapshot is fresh. Derived on the client, never journaled.</summary>
    Unobserved,
}
