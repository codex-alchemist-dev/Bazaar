namespace Bazaar.Server.Git.RepositoryDirectory;

/// <summary>
/// Read-only <see cref="IGitRepositoryDirectory"/> backed by operator configuration (doc 52, Repository directory). It is the
/// fallback until a store-backed directory (the GitHub module) replaces it. Every entry is validated when the directory is built;
/// all problems are reported together in one <see cref="InvalidOperationException"/>, so a bad config never starts half-mapped.
/// </summary>
public sealed class ConfiguredGitRepositoryDirectory : IGitRepositoryDirectory
{
    private readonly Dictionary<string, GitRepositoryInfo> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<GitRepositoryInfo> _ordered;

    public ConfiguredGitRepositoryDirectory(GitRepositoryDirectoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < options.Repositories.Count; i++)
        {
            var entry = options.Repositories[i];
            if (entry is null)
            {
                errors.Add($"Repositories[{i}] is empty.");
                continue;
            }

            // Every entry's id is checked for duplicates, even an entry with other problems, so one run reports all of them.
            var label = $"Repositories[{i}]";
            var before = errors.Count;
            RequireId(entry.RepositoryId, label, errors);
            RequireProvider(entry.Provider, label, errors);
            RequireFullName(entry.FullName, label, errors);
            RequireAbsolutePath(entry.LocalClonePath, label, errors);
            if (!string.IsNullOrWhiteSpace(entry.RepositoryId) && !seenIds.Add(entry.RepositoryId))
                errors.Add($"{label}: RepositoryId '{entry.RepositoryId}' is already configured.");
            if (errors.Count != before)
                continue;

            var info = new GitRepositoryInfo(
                entry.RepositoryId,
                new RepoRef(entry.Provider, entry.FullName),
                entry.LocalClonePath,
                Optional(entry.InstallationId),
                Optional(entry.ProviderRepositoryId));
            _byId.TryAdd(entry.RepositoryId, info);
        }

        if (errors.Count > 0)
            throw new InvalidOperationException($"Invalid Bazaar:Git:Repositories configuration: {string.Join("; ", errors)}");

        _ordered = _byId.Values.OrderBy(static info => info.Repo.Provider, StringComparer.Ordinal).ThenBy(static info => info.Repo.FullName, StringComparer.Ordinal).ToArray();
    }

    /// <inheritdoc />
    public Task<GitRepositoryInfo?> FindAsync(string repositoryId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repositoryId);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<GitRepositoryInfo?>(_byId.TryGetValue(repositoryId, out var info) ? info : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GitRepositoryInfo>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_ordered);
    }

    private static void RequireId(string? repositoryId, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
            errors.Add($"{label}: RepositoryId is required.");
        else if (repositoryId != repositoryId.Trim())
            errors.Add($"{label}: RepositoryId '{repositoryId}' has leading or trailing spaces.");
    }

    private static void RequireProvider(string? provider, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(provider))
            errors.Add($"{label}: Provider is required.");
        else if (provider.Any(char.IsUpper))
            errors.Add($"{label}: Provider '{provider}' must be lowercase.");
    }

    private static void RequireFullName(string? fullName, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            errors.Add($"{label}: FullName is required.");
            return;
        }

        // owner/name, with nested owners allowed (GitLab groups); every segment must be non-blank.
        var segments = fullName.Split('/');
        if (segments.Length < 2 || segments.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{label}: FullName '{fullName}' must be owner/name.");
    }

    private static void RequireAbsolutePath(string? path, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(path))
            errors.Add($"{label}: LocalClonePath is required.");
        else if (!Path.IsPathFullyQualified(path))
            errors.Add($"{label}: LocalClonePath '{path}' must be an absolute path.");
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
