namespace Bazaar.Server.Org;

/// <summary>A project or an agent as the Org pane lists it: an id, a display name and, for agents, an optional role.</summary>
public sealed record OrgDirectoryEntry(Guid Id, string Name, string? Role = null);

/// <summary>
/// Names for the Org pane model (doc 24 section 5, read routes). The module stores relations only, so the composition root
/// supplies the project and agent names. When no directory is registered the model lists none, and the view tolerates an edge
/// that names an agent it does not list.
/// </summary>
public interface IOrgDirectory
{
    Task<IReadOnlyList<OrgDirectoryEntry>> ListProjectsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<OrgDirectoryEntry>> ListAgentsAsync(CancellationToken ct = default);
}

/// <summary>Reads every stored org relation, all scopes at once, for the Org pane model.</summary>
public interface IOrgRelationReader
{
    /// <summary>Lists relations of every scope, oldest first. Ended rows are included when <paramref name="includeEnded"/> is set.</summary>
    Task<IReadOnlyList<OrgRelation>> ListAllAsync(bool includeEnded, CancellationToken ct = default);
}
