namespace Bazaar.Contracts.Kodo;

/// <summary>
/// What a client shows for Kodo (doc 46 section 8.1 and 8.3): the five server states plus the client-derived
/// <see cref="Unobserved"/>. The server never produces <see cref="Unobserved"/>.
/// </summary>
public enum KodoDisplayState
{
    Healthy,
    Degraded,
    Failing,
    Recovering,
    Frozen,

    /// <summary>Kodo has been silent past its budget. Derived on the client, never journaled.</summary>
    Unobserved,
}
