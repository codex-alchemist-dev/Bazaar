using Bazaar.Server.Git;

namespace Bazaar.Server.Athanor;

/// <summary>One operator-configured workspace (<c>Bazaar:Athanor:Workspaces</c>). Id is unique, compared case-sensitively.</summary>
public sealed class EditorWorkspaceEntry
{
    /// <summary>The id clients send, for example <c>vault</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Vault or GitWorktree.</summary>
    public EditorWorkspaceKind Kind { get; set; } = EditorWorkspaceKind.GitWorktree;

    /// <summary>Absolute host-side directory.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>True when saves are refused. Default false.</summary>
    public bool ReadOnly { get; set; }
}

/// <summary>Options of the Athanor module that the host binds from configuration.</summary>
public sealed class AthanorWorkspaceOptions
{
    /// <summary>Configured workspaces (the Vault repo, extra clones, agent worktrees). Empty by default, so nothing is editable until set up.</summary>
    public IList<EditorWorkspaceEntry> Workspaces { get; set; } = [];

    /// <summary>When true, workspaces from the git repository directory (<c>git:&lt;repositoryId&gt;</c>) are read-only. Default false.</summary>
    public bool GitRepositoriesReadOnly { get; set; }
}

/// <summary>Resolves the operator-configured workspaces. Entries with a blank id, a relative path or a duplicate id are ignored.</summary>
public sealed class ConfiguredEditorWorkspaceResolver : IEditorWorkspaceResolver
{
    private readonly Dictionary<string, EditorWorkspaceRoot> _roots = new(StringComparer.Ordinal);

    public ConfiguredEditorWorkspaceResolver(AthanorWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var entry in options.Workspaces)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !Path.IsPathRooted(entry.Path) || _roots.ContainsKey(entry.Id))
            {
                continue;
            }

            _roots[entry.Id] = new EditorWorkspaceRoot(entry.Id, entry.Kind, entry.Path, entry.ReadOnly);
        }
    }

    public ValueTask<EditorWorkspaceRoot?> ResolveAsync(string workspaceId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_roots.TryGetValue(workspaceId, out var root) ? root : null);
}

/// <summary>Resolves <c>git:&lt;repositoryId&gt;</c> to the local clone of a repo in the repository directory (doc 52).</summary>
public sealed class GitRepositoryEditorWorkspaceResolver : IEditorWorkspaceResolver
{
    /// <summary>Prefix of a git workspace id.</summary>
    public const string Prefix = "git:";

    private readonly IGitRepositoryDirectory _directory;
    private readonly bool _readOnly;

    public GitRepositoryEditorWorkspaceResolver(IGitRepositoryDirectory directory, AthanorWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);
        _directory = directory;
        _readOnly = options.GitRepositoriesReadOnly;
    }

    public async ValueTask<EditorWorkspaceRoot?> ResolveAsync(string workspaceId, CancellationToken cancellationToken)
    {
        if (!workspaceId.StartsWith(Prefix, StringComparison.Ordinal) || workspaceId.Length == Prefix.Length)
        {
            return null;
        }

        var repo = await _directory.FindAsync(workspaceId[Prefix.Length..], cancellationToken).ConfigureAwait(false);
        return repo is null || !Path.IsPathRooted(repo.LocalClonePath)
            ? null
            : new EditorWorkspaceRoot(workspaceId, EditorWorkspaceKind.GitWorktree, repo.LocalClonePath, _readOnly);
    }
}

/// <summary>Asks each resolver in order; the first that knows the id wins.</summary>
public sealed class CompositeEditorWorkspaceResolver : IEditorWorkspaceResolver
{
    private readonly IReadOnlyList<IEditorWorkspaceResolver> _resolvers;

    public CompositeEditorWorkspaceResolver(IEnumerable<IEditorWorkspaceResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        _resolvers = [.. resolvers];
    }

    public async ValueTask<EditorWorkspaceRoot?> ResolveAsync(string workspaceId, CancellationToken cancellationToken)
    {
        foreach (var resolver in _resolvers)
        {
            var root = await resolver.ResolveAsync(workspaceId, cancellationToken).ConfigureAwait(false);
            if (root is not null)
            {
                return root;
            }
        }

        return null;
    }
}
