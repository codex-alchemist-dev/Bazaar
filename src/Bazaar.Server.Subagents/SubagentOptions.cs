namespace Bazaar.Server.Subagents;

/// <summary>
/// Operator settings for the harness policy layer. Options may only tighten the runtime limits of doc 06; <see cref="Validate"/>
/// rejects a depth looser than <see cref="RuntimeMaxDepth"/>.
/// </summary>
public sealed record SubagentOptions
{
    /// <summary>
    /// The runtime's depth limit (<c>runtime.subagent_max_depth</c>, doc 06, default 2). The host copies the runtime's configured value
    /// here; <see cref="MaxDepth"/> may only tighten it. Depth and concurrency enforcement stay in the runtime.
    /// </summary>
    public int RuntimeMaxDepth { get; init; } = 2;

    /// <summary><c>harness.subagent_depth</c> (doc 17): 1 by default, at most <see cref="RuntimeMaxDepth"/>.</summary>
    public int MaxDepth { get; init; } = 1;

    /// <summary><c>harness.subagent_disjoint_writers</c> (doc 17): off by default, so one writer per parent at a time.</summary>
    public bool DisjointWriters { get; init; }

    /// <summary>The tool that spawns subagents; it is removed from the allowlist of a child at the depth limit.</summary>
    public string SpawnToolName { get; init; } = "spawn_subagent";

    /// <summary>Tools that change files or the outside world. Read-only and worktree-less children never get them. Matched case-insensitively.</summary>
    public IReadOnlySet<string> WriteToolNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "edit", "write", "apply_patch", "exec", "bash", "shell", "git_commit", "git_push", "push",
    };

    /// <summary>Tools that carry merge or approval authority. A child never gets them, whatever its worktree. Matched case-insensitively.</summary>
    public IReadOnlySet<string> AuthorityToolNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "merge", "merge_pr", "approve", "approve_request", "grant_approval",
    };

    /// <summary>Returns every problem found; empty when the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (RuntimeMaxDepth < 1)
        {
            errors.Add("RuntimeMaxDepth must be at least 1.");
        }

        if (MaxDepth < 1 || MaxDepth > RuntimeMaxDepth)
        {
            errors.Add($"MaxDepth must be between 1 and the runtime limit {RuntimeMaxDepth} (doc 06); options may only tighten.");
        }

        if (string.IsNullOrWhiteSpace(SpawnToolName))
        {
            errors.Add("SpawnToolName must not be blank.");
        }

        if (WriteToolNames is null || WriteToolNames.Count == 0)
        {
            errors.Add("WriteToolNames must not be empty: an empty set would make every child look read-only.");
        }

        if (AuthorityToolNames is null || AuthorityToolNames.Count == 0)
        {
            errors.Add("AuthorityToolNames must not be empty.");
        }

        return errors;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when <see cref="Validate"/> finds a problem.</summary>
    public void EnsureValid()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException("Invalid SubagentOptions: " + string.Join(" ", errors));
        }
    }
}
