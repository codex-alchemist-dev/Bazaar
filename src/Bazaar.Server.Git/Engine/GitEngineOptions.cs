namespace Bazaar.Server.Git;

/// <summary>A name and email for a git signature.</summary>
public sealed record GitSignatureIdentity(string Name, string Email);

/// <summary>
/// Settings for the LibGit2 git engine (doc 52). <see cref="RepoLockTimeout"/> is the <c>git.repo_lock_timeout</c> setting.
/// </summary>
public sealed record GitEngineOptions
{
    /// <summary>How long a call waits for a per-repo lock or merge lane before it fails with <see cref="GitLockTimeoutException"/>.</summary>
    public TimeSpan RepoLockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The directory every agent worktree lives under: <c>{WorktreeRoot}/{provider}/{full name}/{branch}</c>.</summary>
    public string WorktreeRoot { get; set; } = string.Empty;

    /// <summary>Maps a repo to its main clone on the host. Required.</summary>
    public Func<RepoRef, string> RepoRoot { get; set; } = static repo =>
        throw new InvalidOperationException($"GitEngineOptions.RepoRoot is not configured (repo {repo.Provider}:{repo.FullName}).");

    /// <summary>Repos recovery visits at startup, besides the ones found through live worktrees under <see cref="WorktreeRoot"/>.</summary>
    public Func<IReadOnlyList<RepoRef>> KnownRepos { get; set; } = static () => [];

    /// <summary>The default branch of a repo, when the host knows it. Pushes to it are refused; when null the engine reads the clone's remote HEAD.</summary>
    public Func<RepoRef, string?>? DefaultBranch { get; set; }

    /// <summary>Author of agent commits.</summary>
    public GitSignatureIdentity BotAuthor { get; set; } = new("Bazaar Agent", "agent@bazaar.invalid");

    /// <summary>Committer of agent commits: the host service.</summary>
    public GitSignatureIdentity HostCommitter { get; set; } = new("Bazaar Host", "host@bazaar.invalid");

    /// <summary>The remote fetches and pushes use.</summary>
    public string RemoteName { get; set; } = "origin";
}
