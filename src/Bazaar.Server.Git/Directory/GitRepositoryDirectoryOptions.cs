namespace Bazaar.Server.Git.RepositoryDirectory;

/// <summary>
/// Operator-configured repos for <see cref="ConfiguredGitRepositoryDirectory"/> (doc 52, Repository directory). Bound from
/// <c>Bazaar:Git:Repositories</c> by <c>AddBazaarGitRepositoryDirectory</c>. Empty by default.
/// </summary>
public sealed class GitRepositoryDirectoryOptions
{
    /// <summary>The configured repos. Validated when the directory is built.</summary>
    public IList<GitRepositoryEntry> Repositories { get; set; } = [];
}

/// <summary>
/// One configured repo. Fields mirror <see cref="GitRepositoryInfo"/>: <see cref="FullName"/> is <c>owner/name</c> (nested owners
/// allowed for GitLab), <see cref="LocalClonePath"/> is an absolute host-side clone path, and the two optional ids may be left out.
/// </summary>
public sealed class GitRepositoryEntry
{
    /// <summary>The Bazaar repository id the coding harness passes in. Unique, compared case-insensitively.</summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>The provider id, lowercase: <c>github</c>, <c>gitlab</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's full name: <c>owner/name</c>, or the full project path for GitLab.</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>The host-side clone of the repo. Must be an absolute path.</summary>
    public string LocalClonePath { get; set; } = string.Empty;

    /// <summary>The provider's app installation id, when the provider has one. Blank means none.</summary>
    public string? InstallationId { get; set; }

    /// <summary>The provider's own stable repo id or node id. Blank means none.</summary>
    public string? ProviderRepositoryId { get; set; }
}
