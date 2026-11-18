namespace Bazaar.Kodo.Evaluator;

/// <summary>
/// Kodo's health state (doc 46 section 8.1). <see cref="Frozen"/> is incident mode: the underlying
/// health state is still computed and shown beside it (section 8.1 and 16.3). The evaluator's
/// underlying state is never <see cref="Frozen"/>.
/// </summary>
/// <remarks>
/// The client-derived <c>unobserved</c> value (section 8.3) is deliberately not a member: Kodo cannot
/// report its own death, so the server never produces it.
/// </remarks>
public enum KodoState
{
    Healthy,
    Degraded,
    Failing,
    Recovering,
    Frozen,
}
