namespace Bazaar.Roles.Abstractions;

/// <summary>One capability over one scope (doc 48 section 4: a grant is a capability and a scope).</summary>
public readonly record struct Grant(Capability Capability, Scope Scope)
{
    /// <summary>True when this grant already allows everything <paramref name="other"/> allows.</summary>
    public bool Covers(Grant other, IScopeContainment? containment = null) =>
        Capability == other.Capability && Scope.Covers(other.Scope, containment);
}
