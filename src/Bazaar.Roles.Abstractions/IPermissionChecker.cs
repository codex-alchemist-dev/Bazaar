namespace Bazaar.Roles.Abstractions;

/// <summary>Answers "may this principal do this here" from the roles the principal holds (doc 48 section 4).</summary>
public interface IPermissionChecker
{
    /// <summary>
    /// True when any role held by the principal grants <paramref name="capability"/> over a scope that covers
    /// <paramref name="scope"/>. An agent is always refused <c>admin.*</c> and the approval capability, whatever rows say.
    /// Reads current rows on every call, so a change takes effect at the next request.
    /// </summary>
    Task<bool> IsAllowedAsync(Guid principalId, PrincipalKind kind, Capability capability, Scope scope, CancellationToken ct = default);
}
