namespace Bazaar.Server.Git;

/// <summary>
/// Maps a Bazaar repository id to what a host-side consumer needs to work on it (doc 52, Repository directory). The coding harness
/// uses it to find the local clone and the provider identity of a repo without knowing the provider. Read-only; the registry of
/// repos is owned by the module that stores them.
/// </summary>
public interface IGitRepositoryDirectory
{
    /// <summary>The repo for a Bazaar repository id, or null when none is registered.</summary>
    Task<GitRepositoryInfo?> FindAsync(string repositoryId, CancellationToken cancellationToken);

    /// <summary>Every registered repo, in a stable order (provider, full name).</summary>
    Task<IReadOnlyList<GitRepositoryInfo>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// One registered repo. <paramref name="LocalClonePath"/> is the host-side clone (never a VM path). <paramref name="InstallationId"/>
/// is the provider's app installation id when the provider has one (GitHub), else null. <paramref name="ProviderRepositoryId"/> is
/// the provider's own stable repo id or node id, so a rename does not break mappings (doc 21).
/// </summary>
public sealed record GitRepositoryInfo(
    string RepositoryId,
    RepoRef Repo,
    string LocalClonePath,
    string? InstallationId,
    string? ProviderRepositoryId);
