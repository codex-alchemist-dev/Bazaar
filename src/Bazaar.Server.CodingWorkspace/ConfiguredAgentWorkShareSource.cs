using Bazaar.Server.CodingGit;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>
/// The shipped <see cref="IAgentWorkShareSource"/>: <c>&lt;Root&gt;/agents/&lt;agentId&gt;/work</c> and
/// <c>&lt;Root&gt;/agents/&lt;agentId&gt;/passport</c>. The two are siblings, so the work share is never on, under or around the
/// Passport folder. Every result is canonicalised (links resolved) and must stay under the canonical root; an agent id that is
/// not a safe single segment, an unset root, or a link that leaves the root gives null (fail closed).
/// </summary>
public sealed class ConfiguredAgentWorkShareSource : IAgentWorkShareSource
{
    private readonly string? _root;

    /// <summary>Creates the source.</summary>
    /// <param name="options">The operator settings. An unset root is allowed and makes every lookup return null.</param>
    /// <exception cref="InvalidOperationException">The root is set but is relative, a file system root, or blank.</exception>
    public ConfiguredAgentWorkShareSource(CodingWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Root is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.Root) || options.Root.Contains('\0') || !Path.IsPathFullyQualified(options.Root))
        {
            throw new InvalidOperationException("Coding:Workspace:Root must be an absolute path.");
        }

        var full = Path.GetFullPath(options.Root);
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Coding:Workspace:Root must not be a file system root.");
        }

        _root = full;
    }

    /// <inheritdoc />
    public AgentWorkShare? Find(string agentId)
    {
        if (_root is null || !SafeSegment.IsValid(agentId))
        {
            return null;
        }

        try
        {
            var root = WorkSharePaths.Canonicalize(_root);
            var work = WorkSharePaths.Canonicalize(Path.Combine(root, "agents", agentId, "work"));
            var passport = WorkSharePaths.Canonicalize(Path.Combine(root, "agents", agentId, "passport"));
            var inside = Strictly(work, root) && Strictly(passport, root);
            var separate = !WorkSharePaths.IsEqualOrUnder(work, passport) && !WorkSharePaths.IsEqualOrUnder(passport, work);
            return inside && separate ? new AgentWorkShare(work, passport) : null;
        }
        catch (CodingGitException)
        {
            return null;
        }
    }

    private static bool Strictly(string path, string root) =>
        WorkSharePaths.IsEqualOrUnder(path, root) && !string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), WorkSharePaths.Comparison);
}
