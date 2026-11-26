using System.Text.RegularExpressions;
using Bazaar.Server.CodingGit;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>
/// The shipped <see cref="ICodingRepoSource"/>: the operator's list of repositories (<c>Coding:Workspace:Repos</c>). The list is
/// checked when the source is built, so a bad entry stops the host at start rather than at first use. A lookup returns null for an
/// unknown id or a clone that is gone or has moved under the work-share root (fail closed).
/// </summary>
public sealed partial class ConfiguredCodingRepoSource : ICodingRepoSource
{
    private readonly Dictionary<string, CodingRepoRecord> _repos = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _root;

    /// <summary>Creates the source.</summary>
    /// <param name="options">The operator settings.</param>
    /// <exception cref="InvalidOperationException">An entry is invalid or two entries share an id.</exception>
    public ConfiguredCodingRepoSource(CodingWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = string.IsNullOrWhiteSpace(options.Root) || !Path.IsPathFullyQualified(options.Root)
            ? null
            : WorkSharePaths.Canonicalize(options.Root);
        foreach (var entry in options.Repos ?? [])
        {
            var record = Validate(entry);
            if (!_repos.TryAdd(record.Repo.Id, record))
            {
                throw new InvalidOperationException($"Coding:Workspace:Repos lists the repository id '{record.Repo.Id}' twice.");
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<CodingRepoRecord?> FindAsync(string repoId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SafeSegment.IsValid(repoId) || !_repos.TryGetValue(repoId, out var record))
        {
            return ValueTask.FromResult<CodingRepoRecord?>(null);
        }

        // The clone may have been removed, or replaced by a link into the work share, since start.
        if (!Directory.Exists(record.ClonePath) || IsUnderRoot(record.ClonePath))
        {
            return ValueTask.FromResult<CodingRepoRecord?>(null);
        }

        return ValueTask.FromResult<CodingRepoRecord?>(record);
    }

    private bool IsUnderRoot(string clonePath)
    {
        if (_root is null)
        {
            return false;
        }

        try
        {
            return WorkSharePaths.IsEqualOrUnder(WorkSharePaths.Canonicalize(clonePath), _root)
                || WorkSharePaths.IsEqualOrUnder(_root, WorkSharePaths.Canonicalize(clonePath));
        }
        catch (CodingGitException)
        {
            return true;
        }
    }

    private CodingRepoRecord Validate(CodingRepoOptions entry)
    {
        if (entry is null || !SafeSegment.IsValid(entry.Id))
        {
            throw new InvalidOperationException("A Coding:Workspace:Repos entry needs an id of [A-Za-z0-9_][A-Za-z0-9._-]{0,99}.");
        }

        var id = entry.Id!;
        if (string.IsNullOrWhiteSpace(entry.DefaultBranch) || entry.DefaultBranch.Any(char.IsControl) || entry.DefaultBranch.StartsWith('-'))
        {
            throw new InvalidOperationException($"Repository '{id}' has an invalid default branch.");
        }

        if (string.IsNullOrWhiteSpace(entry.ClonePath) || entry.ClonePath.Contains('\0') || !Path.IsPathFullyQualified(entry.ClonePath))
        {
            throw new InvalidOperationException($"Repository '{id}' needs an absolute clone path.");
        }

        if (!NamePattern().IsMatch(entry.Owner ?? string.Empty) || !NamePattern().IsMatch(entry.Name ?? string.Empty))
        {
            throw new InvalidOperationException($"Repository '{id}' needs a GitHub owner and name of [A-Za-z0-9._-].");
        }

        if (entry.InstallationId <= 0 || entry.RepositoryId <= 0)
        {
            throw new InvalidOperationException($"Repository '{id}' needs a positive installation id and repository id.");
        }

        var clone = Path.GetFullPath(entry.ClonePath);
        if (IsUnderRoot(clone))
        {
            throw new InvalidOperationException($"Repository '{id}' has a clone path inside the work-share root.");
        }

        return new CodingRepoRecord(new RepoRef(id, entry.DefaultBranch), clone, entry.InstallationId, entry.Owner!, entry.Name!, entry.RepositoryId);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
