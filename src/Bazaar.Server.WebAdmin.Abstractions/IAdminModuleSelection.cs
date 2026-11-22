namespace Bazaar.Server.WebAdmin;

public sealed record AdminModuleInfo(string Id, string Name, string Version, string Tag, string[] DependsOn, bool Selected);
public sealed record AdminModuleCatalogue(long Revision, IReadOnlyList<AdminModuleInfo> Modules);

public sealed record AdminSelectionProblem(string ModuleId, string Message);
public sealed record AdminSelectionPreview(bool Valid, IReadOnlyList<AdminSelectionProblem> Problems, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

public sealed record AdminSelectionCommit(bool Committed, long Revision, IReadOnlyList<AdminSelectionProblem> Problems);

public sealed record AdminBuildRecord(string BuildId, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? ReportSummary, int Attempt);

public sealed record AdminRebuildStart(bool Started, string? BuildId, string? Reason);

/// <summary>
/// The module-selection and rebuild port (doc 51 section 5), implemented by the module manager and the company build module.
/// The panel only starts a rebuild and shows its state; the build module owns the work (doc 37 section 4a).
/// </summary>
public interface IAdminModuleSelection
{
    Task<AdminModuleCatalogue> GetCatalogueAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdminBuildRecord>> GetBuildsAsync(CancellationToken ct = default);

    /// <summary>Dry run. Writes nothing.</summary>
    Task<AdminSelectionPreview> PreviewAsync(IReadOnlyList<string> selectedModuleIds, CancellationToken ct = default);

    /// <summary>Commits against <paramref name="expectedRevision"/>. A stale revision returns <c>Committed = false</c> with revision set to the current one.</summary>
    Task<AdminSelectionCommit> CommitAsync(Guid actorPrincipalId, IReadOnlyList<string> selectedModuleIds, long expectedRevision, CancellationToken ct = default);

    /// <summary>Starts a rebuild. Returns <c>Started = false</c> when one is already running. The same idempotency key returns the first result.</summary>
    Task<AdminRebuildStart> StartRebuildAsync(Guid actorPrincipalId, string idempotencyKey, CancellationToken ct = default);
}
