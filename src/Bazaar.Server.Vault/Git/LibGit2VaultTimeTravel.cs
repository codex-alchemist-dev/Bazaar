using LibGit2Sharp;

namespace Bazaar.Server.Vault.Git;

/// <summary>
/// The default <see cref="IVaultTimeTravel"/> over LibGit2Sharp. Commit time means the committer time, not the author
/// time: it is when the commit entered this repository, so it stays ordered with the writes the Vault made. Each call
/// opens and disposes its own <see cref="Repository"/>.
/// </summary>
public sealed class LibGit2VaultTimeTravel : IVaultTimeTravel
{
    public Task<string?> NearestCommitAtOrBeforeAsync(string repoPath, DateTimeOffset at, CancellationToken ct) =>
        Task.Run(() =>
        {
            using var repo = new Repository(Path.GetFullPath(repoPath));
            var tip = repo.Head.Tip;
            if (tip is null)
            {
                return null;
            }

            var filter = new CommitFilter { IncludeReachableFrom = tip, SortBy = CommitSortStrategies.Topological };
            Commit? best = null;
            foreach (var commit in repo.Commits.QueryBy(filter))
            {
                ct.ThrowIfCancellationRequested();
                // Strictly newer only, so a tie keeps the commit found first.
                if (commit.Committer.When <= at && (best is null || commit.Committer.When > best.Committer.When))
                {
                    best = commit;
                }
            }

            return best?.Sha;
        }, ct);

    public Task CreateBranchFromCommitAsync(string repoPath, string branch, string commitSha, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        return Task.Run(() =>
        {
            // A leading dash passes the ref-name rules but the engine's branch check refuses it, so refuse it here.
            if (branch.StartsWith('-') || !Reference.IsValidName("refs/heads/" + branch))
            {
                throw new ArgumentException($"'{branch}' is not a valid branch name.", nameof(branch));
            }

            using var repo = new Repository(Path.GetFullPath(repoPath));
            var commit = Find(repo, commitSha) ?? throw new ArgumentException($"Commit '{commitSha}' not found.", nameof(commitSha));
            if (repo.Branches[branch] is not null)
            {
                throw new InvalidOperationException($"Branch '{branch}' already exists.");
            }

            repo.CreateBranch(branch, commit);
        }, ct);
    }

    private static Commit? Find(Repository repo, string commitSha)
    {
        try
        {
            return repo.Lookup<Commit>(commitSha);
        }
        catch (LibGit2SharpException)
        {
            return null;
        }
    }
}
