using Bazaar.Server.Wire.Connection;

namespace Bazaar.Server.Components.Remote;

/// <summary>Why a suspend or unsuspend did or did not change anything.</summary>
public enum PersonSuspensionOutcome
{
    /// <summary>The person's state changed.</summary>
    Done,

    /// <summary>No such person.</summary>
    NotFound,

    /// <summary>The person is already in the requested state, or is removed. Nothing changed and nothing was audited.</summary>
    NoChange,

    /// <summary>Refused by rule: an owner cannot be suspended, and nobody suspends themselves.</summary>
    NotAllowed,
}

/// <summary>
/// Suspends and unsuspends a person (doc 32 person suspension; PROPOSED, coordinator 2026-11-22). Suspending sets <c>human.status</c> to
/// <c>suspended</c>, revokes every device and approval key of the person (so refresh credentials stop working), and is audited. A
/// suspended person cannot sign in or refresh. Unsuspending sets the person active again; revoked devices stay revoked, so the person pairs again.
/// The caller has checked the <c>admin.members</c> capability.
/// </summary>
public interface IPersonSuspension
{
    Task<PersonSuspensionOutcome> SuspendAsync(Guid humanId, PrincipalRef by, CancellationToken ct = default);

    Task<PersonSuspensionOutcome> UnsuspendAsync(Guid humanId, PrincipalRef by, CancellationToken ct = default);
}
