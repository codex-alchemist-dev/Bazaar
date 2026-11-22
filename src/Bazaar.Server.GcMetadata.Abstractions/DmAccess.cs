using Bazaar.Contracts.Gc;

namespace Bazaar.Server.GcMetadata;

/// <summary>Who is acting on a DM: a principal and its kind. An agent's request or decision is never human consent.</summary>
public readonly record struct DmActor(Guid Id, PrincipalKind Kind);

/// <summary>A DM as the access flow sees it: its id and its active members (not the owner unless the owner is a member).</summary>
public sealed record DmInfo(Guid GcId, IReadOnlyCollection<Guid> ActiveMembers);

/// <summary>State of a DM access request.</summary>
public enum DmAccessStatus
{
    /// <summary>Waiting for a decision.</summary>
    Pending,

    /// <summary>The requester may read the DM.</summary>
    Approved,

    /// <summary>Refused.</summary>
    Denied,
}

/// <summary>A DM access request (<c>gc_access_request</c>).</summary>
public sealed record DmAccessRequest(Guid Id, Guid GcId, Guid Requester, DmAccessStatus Status, Guid? DecidedBy, DateTimeOffset CreatedAt);

/// <summary>Who may decide a request. Mirrors the setting <c>gc.dm_access_approvers</c>; the owner is always an approver.</summary>
public enum DmApprovers
{
    /// <summary>Only the owner.</summary>
    Owner,

    /// <summary>The owner and the other active members of the DM.</summary>
    OwnerOrDmMember,
}

/// <summary>Settings the flow reads.</summary>
/// <param name="Approvers">The value of <c>gc.dm_access_approvers</c>.</param>
/// <param name="ShowOwnerReadMarker">The value of <c>privacy.owner_dm_read_marker</c>; default on.</param>
public sealed record DmAccessOptions(DmApprovers Approvers = DmApprovers.Owner, bool ShowOwnerReadMarker = true);

/// <summary>Storage of requests. The Postgres form sits over <c>gc_access_request</c>.</summary>
public interface IDmAccessLedger
{
    /// <summary>Every request for one DM, oldest first, in any state.</summary>
    Task<IReadOnlyList<DmAccessRequest>> ListAsync(Guid gcId, CancellationToken ct);

    /// <summary>One request, or null.</summary>
    Task<DmAccessRequest?> GetAsync(Guid requestId, CancellationToken ct);

    /// <summary>Records a pending request and returns it.</summary>
    Task<DmAccessRequest> CreateAsync(Guid gcId, Guid requester, DateTimeOffset at, CancellationToken ct);

    /// <summary>Resolves a pending request once. Returns false when it is unknown or already resolved.</summary>
    Task<bool> ResolveAsync(Guid requestId, DmAccessStatus decision, Guid decidedBy, DateTimeOffset at, CancellationToken ct);
}

/// <summary>Finds a DM. Returns null when the GC does not exist or is not a DM.</summary>
public interface IDmDirectory
{
    /// <summary>The DM with this id, or null.</summary>
    Task<DmInfo?> FindAsync(Guid gcId, CancellationToken ct);
}

/// <summary>Writes the <c>system</c> event in the DM's log for a request or a decision, so both members see it.</summary>
public interface IDmAccessEvents
{
    /// <summary>Appends one system event. <paramref name="kind"/> is <c>access_requested</c>, <c>access_granted</c> or <c>access_denied</c>.</summary>
    Task AppendAsync(Guid gcId, string kind, Guid requestId, Guid requester, Guid actor, CancellationToken ct);
}

/// <summary>What happened to a request or a decision.</summary>
public enum DmAccessOutcome
{
    /// <summary>A new request was recorded.</summary>
    Requested,

    /// <summary>A request was already pending; it is returned unchanged.</summary>
    AlreadyPending,

    /// <summary>The principal already reads this DM (owner, member, or earlier grant).</summary>
    AlreadyAllowed,

    /// <summary>The GC does not exist or is not a DM.</summary>
    NotADm,

    /// <summary>The request was approved.</summary>
    Granted,

    /// <summary>The request was denied.</summary>
    Denied,

    /// <summary>The actor may not decide: an agent, a member when only the owner may, or the requester.</summary>
    NotAllowed,

    /// <summary>The request is unknown or was already resolved.</summary>
    NotPending,
}

/// <summary>Result of a request or decision.</summary>
public sealed record DmAccessResult(DmAccessOutcome Outcome, DmAccessRequest? Request);

/// <summary>Why a read of a DM was allowed.</summary>
public enum DmReadBasis
{
    /// <summary>Not allowed.</summary>
    None,

    /// <summary>The viewer is an active member.</summary>
    Member,

    /// <summary>The viewer is the owner and not a member.</summary>
    Owner,

    /// <summary>The viewer holds an approved request.</summary>
    Grant,
}

/// <summary>Result of <see cref="IDmReadAuthorizer.AuthorizeReadAsync"/>.</summary>
/// <param name="Allowed">True when the viewer may read.</param>
/// <param name="Basis">Why.</param>
/// <param name="ShowOwnerReadMarker">True when the read is an owner read and the marker setting is on, so clients show that the owner can read this DM.</param>
public sealed record DmReadDecision(bool Allowed, DmReadBasis Basis, bool ShowOwnerReadMarker);

/// <summary>Decides whether a principal may read a DM, and audits owner reads.</summary>
public interface IDmReadAuthorizer
{
    /// <summary>Decides and, for an owner read of a DM the owner is not in, writes the audit entry before returning.</summary>
    Task<DmReadDecision> AuthorizeReadAsync(DmActor viewer, Guid gcId, CancellationToken ct);
}

/// <summary>Files requests and applies decisions for DM access.</summary>
public interface IDmAccessService
{
    /// <summary>A non-member asks to read a DM.</summary>
    Task<DmAccessResult> RequestAsync(DmActor requester, Guid gcId, CancellationToken ct);

    /// <summary>The owner, or an allowed member, grants or denies a pending request.</summary>
    Task<DmAccessResult> DecideAsync(DmActor decider, Guid requestId, bool approve, CancellationToken ct);
}
