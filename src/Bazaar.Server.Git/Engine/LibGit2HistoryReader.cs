using LibGit2Sharp;

namespace Bazaar.Server.Git;

/// <summary>
/// The default <see cref="IGitHistoryReader"/> (doc 52). Reads the repo's main clone through LibGit2Sharp under the same
/// per-repo lock the writers use, and keeps the hold short by capping the walk at <see cref="GitEngineOptions.MaxHistoryCommits"/>.
/// </summary>
public sealed class LibGit2HistoryReader : IGitHistoryReader
{
    private const int _maxSubjectLength = 200;

    private readonly GitEngineOptions _options;
    private readonly RepoLocks _locks;

    public LibGit2HistoryReader(GitEngineOptions options, RepoLocks locks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(locks);
        _options = options;
        _locks = locks;
    }

    public async Task<IReadOnlyList<GitRefInfo>> ListRefsAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var root = _options.RepoRoot(repo);
        var remotePrefix = $"refs/remotes/{_options.RemoteName}/";

        using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(root), cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                using var git = new Repository(root);
                var local = new Dictionary<string, string>(StringComparer.Ordinal);
                var remote = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var branch in git.Branches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (branch.Tip is null)
                    {
                        continue;
                    }

                    if (!branch.IsRemote)
                    {
                        local[branch.CanonicalName["refs/heads/".Length..]] = branch.Tip.Sha;
                    }
                    else if (branch.CanonicalName.StartsWith(remotePrefix, StringComparison.Ordinal))
                    {
                        var name = branch.CanonicalName[remotePrefix.Length..];
                        if (name != "HEAD")
                        {
                            remote[name] = branch.Tip.Sha;
                        }
                    }
                }

                // A local branch wins over a remote-tracking branch of the same name.
                var refs = local.Select(b => new GitRefInfo("branch", b.Key, b.Value)).ToList();
                refs.AddRange(remote.Where(b => !local.ContainsKey(b.Key)).Select(b => new GitRefInfo("branch", b.Key, b.Value)));
                foreach (var tag in git.Tags)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Annotated tags peel to their commit; a tag on a tree or blob is not a history tip.
                    if (tag.PeeledTarget is Commit commit)
                    {
                        refs.Add(new GitRefInfo("tag", tag.FriendlyName, commit.Sha));
                    }
                }

                refs.Sort(static (a, b) =>
                {
                    var byName = string.CompareOrdinal(a.Name, b.Name);
                    return byName != 0 ? byName : string.CompareOrdinal(a.Kind, b.Kind);
                });
                return (IReadOnlyList<GitRefInfo>)refs;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GitCommitInfo>> ReadHistoryAsync(RepoRef repo, string tipSha, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrEmpty(tipSha) || tipSha.Length is < 7 or > 40 || !tipSha.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The tip must be a 7 to 40 character hex sha.", nameof(tipSha));
        }

        var take = Math.Clamp(limit, 1, Math.Max(1, _options.MaxHistoryCommits));
        var root = _options.RepoRoot(repo);

        using var _ = await _locks.AcquireAsync(RepoLocks.KeyForPath(root), cancellationToken).ConfigureAwait(false);
        return await Task.Run(
            () =>
            {
                using var git = new Repository(root);
                Commit tip;
                try
                {
                    tip = git.Lookup<Commit>(tipSha) ?? throw new GitEngineException($"Commit '{tipSha}' not found.");
                }
                catch (AmbiguousSpecificationException)
                {
                    throw new GitEngineException($"Commit '{tipSha}' is ambiguous.");
                }

                var filter = new CommitFilter
                {
                    IncludeReachableFrom = tip,
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time,
                };
                var result = new List<GitCommitInfo>(Math.Min(take, 256));
                foreach (var commit in git.Commits.QueryBy(filter))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(new GitCommitInfo(
                        commit.Sha,
                        commit.Parents.Select(p => p.Sha).ToList(),
                        commit.Author.Name,
                        commit.Author.When,
                        commit.Committer.When,
                        Subject(commit.Message)));
                    if (result.Count >= take)
                    {
                        break;
                    }
                }

                return (IReadOnlyList<GitCommitInfo>)result;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static string Subject(string message)
    {
        var newline = message.IndexOfAny(['\r', '\n']);
        var line = (newline < 0 ? message : message[..newline]).Trim();
        return line.Length <= _maxSubjectLength ? line : line[.._maxSubjectLength];
    }
}
