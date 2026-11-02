namespace Bazaar.Server.Identity.GitHubApp;

/// <summary>
/// Asks the host to mint one GitHub App installation token, narrowed to some repositories and permissions (doc 14,
/// "GitHub Apps"). The constructor validates the request and copies its collections, so a constructed request is always
/// well formed.
/// </summary>
public sealed record GitHubAppTokenRequest
{
    /// <summary>
    /// The hard cap on any requested lifetime. GitHub documents installation tokens as lasting one hour. Doc 14 and doc 21
    /// (test T4) still list this as unverified, so the cap stays until T4 passes. Do not raise it without that check.
    /// </summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(1);

    /// <summary>Creates and validates a request.</summary>
    /// <param name="installationId">The App installation the token is minted for. Must be positive.</param>
    /// <param name="repositories">Bare repository names without the owner, as the GitHub API expects them. At least one.</param>
    /// <param name="permissions">GitHub permission name to access level, for example <c>contents</c> to <c>write</c>. At least one.</param>
    /// <param name="lifetime">The usable window the host may rely on. Must be positive and at most <see cref="MaxLifetime"/>.</param>
    /// <exception cref="ArgumentException">A repository or permission is empty, or the repository or permission set is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The installation id or lifetime is out of range.</exception>
    public GitHubAppTokenRequest(
        long installationId,
        IReadOnlyList<string> repositories,
        IReadOnlyDictionary<string, string> permissions,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(permissions);

        if (installationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(installationId), installationId, "The installation id must be positive.");
        }

        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "The lifetime must be positive and at most one hour.");
        }

        if (repositories.Count == 0)
        {
            throw new ArgumentException("At least one repository is required. An empty list would widen the token.", nameof(repositories));
        }

        var copiedRepositories = new List<string>(repositories.Count);
        foreach (var repository in repositories)
        {
            if (string.IsNullOrWhiteSpace(repository))
            {
                throw new ArgumentException("Repository names must not be empty.", nameof(repositories));
            }

            if (!copiedRepositories.Contains(repository, StringComparer.OrdinalIgnoreCase))
            {
                copiedRepositories.Add(repository);
            }
        }

        if (permissions.Count == 0)
        {
            throw new ArgumentException("At least one permission is required. GitHub would otherwise grant every installation permission.", nameof(permissions));
        }

        var copiedPermissions = new Dictionary<string, string>(permissions.Count, StringComparer.Ordinal);
        foreach (var (name, level) in permissions)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(level))
            {
                throw new ArgumentException("Permission names and access levels must not be empty.", nameof(permissions));
            }

            copiedPermissions[name] = level;
        }

        InstallationId = installationId;
        Repositories = copiedRepositories;
        Permissions = copiedPermissions;
        Lifetime = lifetime;
    }

    /// <summary>The App installation the token is minted for.</summary>
    public long InstallationId { get; }

    /// <summary>The bare repository names the token is narrowed to. De-duplicated, case-insensitively.</summary>
    public IReadOnlyList<string> Repositories { get; }

    /// <summary>The permission names and access levels the token is narrowed to.</summary>
    public IReadOnlyDictionary<string, string> Permissions { get; }

    /// <summary>The usable window the host may rely on.</summary>
    public TimeSpan Lifetime { get; }
}
