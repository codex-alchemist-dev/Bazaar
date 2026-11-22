using Bazaar.Core;
using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild.State;

/// <summary>The stored company selection. Revision 0 is the empty selection nobody has committed yet.</summary>
/// <param name="Revision">Bumped by every commit.</param>
/// <param name="SelectedModuleIds">The committed module ids, sorted.</param>
/// <param name="PendingApprovals">Module id to the approval-queue item filed for it, for modules that need the owner before they are built in.</param>
/// <param name="UpdatedBy">The admin who committed it.</param>
/// <param name="UpdatedAt">When it was committed.</param>
public sealed record SelectionState(
    long Revision,
    IReadOnlyList<string> SelectedModuleIds,
    IReadOnlyDictionary<string, Guid> PendingApprovals,
    Guid? UpdatedBy,
    DateTimeOffset? UpdatedAt);

/// <summary>One build history row. <see cref="Status"/> is <c>running</c>, <c>succeeded</c> or <c>failed</c> (<c>queued</c> is reserved).</summary>
public sealed record BuildRow(
    Guid Id,
    long SelectionRevision,
    string Status,
    Guid StartedBy,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string IdempotencyKey,
    int Attempts,
    string? ResultSummary);

/// <summary>What <see cref="ICompanyBuildStateStore.TryBeginBuildAsync"/> did.</summary>
public enum BuildBeginOutcome
{
    Started,

    /// <summary>The idempotency key already has a build. <see cref="BuildBeginResult.Existing"/> is it.</summary>
    DuplicateKey,

    /// <summary>Another build is running.</summary>
    AlreadyRunning,
}

public sealed record BuildBeginResult(BuildBeginOutcome Outcome, BuildRow? Existing);

/// <summary>
/// The store seam for the selection and the build history. The Postgres implementation is <see cref="PostgresCompanyBuildStateStore"/>;
/// tests use an in-memory one with the same guarded transitions.
/// </summary>
public interface ICompanyBuildStateStore
{
    Task<SelectionState> GetSelectionAsync(CancellationToken ct = default);

    /// <summary>Atomic compare-and-set on revision. Returns the new state, or null when <paramref name="expectedRevision"/> is stale.</summary>
    Task<SelectionState?> TryCommitSelectionAsync(
        long expectedRevision,
        IReadOnlyList<string> selectedModuleIds,
        IReadOnlyDictionary<string, Guid> pendingApprovals,
        Guid actor,
        DateTimeOffset now,
        CancellationToken ct = default);

    /// <summary>Replaces the pending approvals of the given revision without bumping it. False when the revision has moved.</summary>
    Task<bool> TrySetPendingApprovalsAsync(long revision, IReadOnlyDictionary<string, Guid> pendingApprovals, CancellationToken ct = default);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<BuildRow>> ListBuildsAsync(int limit, CancellationToken ct = default);

    Task<BuildRow?> GetBuildByKeyAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>Inserts a running build. A repeated key or a second running build is reported, never thrown.</summary>
    Task<BuildBeginResult> TryBeginBuildAsync(BuildRow running, CancellationToken ct = default);

    Task CompleteBuildAsync(Guid id, bool succeeded, DateTimeOffset finishedAt, int attempts, string summary, CancellationToken ct = default);

    /// <summary>Marks builds still running as failed. Used once per process: a build cannot be running from a previous run.</summary>
    Task<int> FailRunningBuildsAsync(DateTimeOffset now, string summary, CancellationToken ct = default);
}

/// <summary>Every module known to this server build, read from the component manifests (docs/design/BAZAAR-COMPONENTS.md).</summary>
public interface IModuleCatalogue
{
    IReadOnlyList<ComponentManifest> Manifests { get; }
}

/// <summary>
/// Whether a module still needs the owner before it is built in. It is backed by the Bazaar.Install gates (signature and
/// permission approval, doc 48 section 1): true while the module's declared permissions lack the owner's signed approval.
/// Optional: with none registered, no module needs approval.
/// </summary>
public interface IModuleApprovalRequirement
{
    Task<bool> NeedsOwnerApprovalAsync(string moduleId, CancellationToken ct = default);
}

/// <summary>
/// Runs one company build for a composed plan, through the existing <c>CompanyBuildPipeline</c> (targets, identity and output
/// folder are host settings). Optional: with none registered, a rebuild is refused with a reason.
/// </summary>
public interface ICompanyBuildExecutor
{
    Task<CompanyBuildSetReport> RunAsync(CompanyBuildPlan plan, CancellationToken ct = default);
}

/// <summary>Settings for the state module. <see cref="CompanyId"/> comes from <c>Bazaar:CompanyBuild:CompanyId</c>.</summary>
public sealed record CompanyBuildStateOptions(string CompanyId = "default", int HistoryLimit = 50);
