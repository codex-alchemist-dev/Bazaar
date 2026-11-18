using Bazaar.Contracts.Gc;

namespace Bazaar.Server.TimeMachine;

/// <summary>A request to fork a project GC from a point in a source GC.</summary>
/// <param name="RequestId">Caller-chosen idempotency key. Repeating the same (source, seq, request id) returns the first fork.</param>
/// <param name="SourceGcId">The GC to fork from.</param>
/// <param name="SourceSeq">The seq to fork at. Clamped to the requester's grant.</param>
/// <param name="Requester">Who asks. Must be able to read the source.</param>
public sealed record ForkRequest(Guid RequestId, Guid SourceGcId, long SourceSeq, GcPrincipal Requester);

/// <summary>An append-only link from a forked GC back to its source (doc 26 <c>fork_link</c>). No messages are copied.</summary>
/// <param name="NewGcId">The new project GC.</param>
/// <param name="SourceGcId">The source GC.</param>
/// <param name="SourceSeq">The seq forked at.</param>
/// <param name="VaultCommit">The Vault commit the fork branches from, or null when the Vault is not installed.</param>
/// <param name="RequestId">The idempotency key of the request that made it.</param>
/// <param name="CreatedAt">When the link was written.</param>
public sealed record ForkLink(Guid NewGcId, Guid SourceGcId, long SourceSeq, string? VaultCommit, Guid RequestId, DateTimeOffset CreatedAt);

/// <summary>The result of a fork.</summary>
/// <param name="Link">The link, new or the earlier one.</param>
/// <param name="Created">False when the request was a repeat and the earlier fork was returned.</param>
public sealed record ForkResult(ForkLink Link, bool Created);

/// <summary>Forks a GC at a seq (docs/design/26, section 4). Writes one link and copies no messages.</summary>
public interface IForkService
{
    /// <summary>Forks. Idempotent on (source GC, source seq, request id). Throws <c>GcAccessDeniedException</c> when the requester may not read the source.</summary>
    Task<ForkResult> ForkAsync(ForkRequest request, CancellationToken ct);
}

/// <summary>Persists fork links. Append-only: there is no update or delete.</summary>
public interface IForkLinkStore
{
    /// <summary>Finds the link for a request, or null.</summary>
    Task<ForkLink?> FindAsync(Guid sourceGcId, long sourceSeq, Guid requestId, CancellationToken ct);

    /// <summary>Appends the link, or returns the existing one when (source, seq, request id) is already present.</summary>
    /// <returns>The stored link and whether this call created it.</returns>
    Task<(ForkLink Link, bool Created)> AppendAsync(ForkLink link, CancellationToken ct);

    /// <summary>Links whose source is <paramref name="sourceGcId"/>, oldest first.</summary>
    Task<IReadOnlyList<ForkLink>> ListBySourceAsync(Guid sourceGcId, CancellationToken ct);
}

/// <summary>
/// Creates the empty project GC and Vault branch a fork lands in. The GC module owns GC creation, so this is a seam the host
/// binds to it; the time machine copies nothing into the new GC.
/// </summary>
public interface IForkTargetProvisioner
{
    /// <summary>Creates the target for a fork of <paramref name="sourceGcId"/> at <paramref name="effectiveSeq"/>.</summary>
    Task<ForkTarget> ProvisionAsync(Guid sourceGcId, long effectiveSeq, GcPrincipal requester, CancellationToken ct);
}

/// <summary>What a provisioner made.</summary>
/// <param name="NewGcId">The new project GC.</param>
/// <param name="VaultCommit">The Vault commit the branch starts from, or null when the Vault is not installed.</param>
public sealed record ForkTarget(Guid NewGcId, string? VaultCommit);
