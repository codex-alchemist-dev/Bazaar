namespace Bazaar.Roles.Abstractions;

/// <summary>The kinds of principal a role can be granted to (doc 48 section 4: human, agent, service).</summary>
public enum PrincipalKind
{
    Human,
    Agent,
    Service,
}
