namespace Cdax.Core;

/// <summary>
/// Who performed an action and, optionally, on whose behalf, for example a service acting for a signed-in person
/// or an agent acting for a user. Audit entries carry both.
/// </summary>
/// <param name="Actor">The principal that performed the action.</param>
/// <param name="OnBehalfOf">The principal the actor was acting for, or null when it acted for itself.</param>
public sealed record PrincipalAction(Principal Actor, Principal? OnBehalfOf = null)
{
    /// <summary>A short human-readable form: <c>actor</c>, or <c>actor on behalf of principal</c>.</summary>
    public override string ToString() => OnBehalfOf is null ? Actor.ToString() : $"{Actor} on behalf of {OnBehalfOf}";
}
