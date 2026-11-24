namespace Bazaar.Server.CodingWorkspace;

/// <summary>One repository the operator lets the harness work in (setting <c>Coding:Workspace:Repos</c>).</summary>
public sealed class CodingRepoOptions
{
    /// <summary>Short repository id, <c>[A-Za-z0-9_][A-Za-z0-9._-]{0,99}</c>. It becomes a directory name.</summary>
    public string? Id { get; set; }

    /// <summary>The default branch, for example <c>main</c>.</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>Absolute path of the host-side clone. It must not be under the work-share root.</summary>
    public string? ClonePath { get; set; }

    /// <summary>The GitHub App installation id. Must be positive.</summary>
    public long InstallationId { get; set; }

    /// <summary>The GitHub owner.</summary>
    public string? Owner { get; set; }

    /// <summary>The GitHub repository name.</summary>
    public string? Name { get; set; }

    /// <summary>The numeric GitHub repository id. Must be positive.</summary>
    public long RepositoryId { get; set; }
}

/// <summary>
/// Operator settings for the coding workspace sources (doc 17, "Workspace sources"). There is no default root: an unset root
/// makes every share lookup fail closed rather than guess a location.
/// </summary>
public sealed class CodingWorkspaceOptions
{
    /// <summary>
    /// Absolute root that holds <c>agents/&lt;agentId&gt;/work</c> and <c>agents/&lt;agentId&gt;/passport</c>
    /// (setting <c>Coding:Workspace:Root</c>).
    /// </summary>
    public string? Root { get; set; }

    /// <summary>The repositories the harness may use (setting <c>Coding:Workspace:Repos</c>).</summary>
    public List<CodingRepoOptions> Repos { get; set; } = [];
}
