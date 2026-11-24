using Bazaar.RyuseiFuda.Kanban;
using Bazaar.Server.Agents.Data;

namespace Bazaar.Server.Org;

/// <summary>
/// The Org pane's names (doc 24 section 2, "Org directory"), read from the Agents and Kanban modules through their read-only
/// seams. Agent roles come from the agent record (null when unset). Projects are Kanban boards (one board is one project);
/// with no board reader registered, none are listed.
/// </summary>
public sealed class AgentOrgDirectory(IAgentDirectoryReader agents, IBoardDirectoryReader? boards = null) : IOrgDirectory
{
    public async Task<IReadOnlyList<OrgDirectoryEntry>> ListProjectsAsync(CancellationToken ct = default)
    {
        if (boards is null)
        {
            return [];
        }

        var listed = await boards.ListBoardsAsync(ct).ConfigureAwait(false);
        return [.. listed.Select(b => new OrgDirectoryEntry(b.BoardId, b.Name))];
    }

    public async Task<IReadOnlyList<OrgDirectoryEntry>> ListAgentsAsync(CancellationToken ct = default)
    {
        var listed = await agents.ListActiveAgentsAsync(ct).ConfigureAwait(false);
        return [.. listed.Select(a => new OrgDirectoryEntry(a.AgentId, a.Name, a.Role))];
    }
}
