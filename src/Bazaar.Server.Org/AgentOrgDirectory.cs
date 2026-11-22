using Bazaar.Server.Agents.Data;

namespace Bazaar.Server.Org;

/// <summary>
/// The Org pane's names (doc 24 section 2, "Org directory"), read from the Agents module through its read-only seam. Agent
/// roles are not stored anywhere yet, so entries carry names only. No project registry exists yet, so no projects are listed.
/// </summary>
public sealed class AgentOrgDirectory(IAgentDirectoryReader agents) : IOrgDirectory
{
    public Task<IReadOnlyList<OrgDirectoryEntry>> ListProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgDirectoryEntry>>([]);

    public async Task<IReadOnlyList<OrgDirectoryEntry>> ListAgentsAsync(CancellationToken ct = default)
    {
        var listed = await agents.ListActiveAgentsAsync(ct).ConfigureAwait(false);
        return [.. listed.Select(a => new OrgDirectoryEntry(a.AgentId, a.Name))];
    }
}
