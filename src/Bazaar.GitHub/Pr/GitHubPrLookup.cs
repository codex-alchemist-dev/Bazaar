using Bazaar.GitHub.Events;
using Npgsql;

namespace Bazaar.GitHub.Pr;

/// <summary>An open PR found in the <c>github_pr</c> read model.</summary>
public sealed record GitHubPrMatch(string PrNodeId, int Number, string? AuthorLogin);

/// <summary>
/// Finds the open PR that a <c>check_run</c>, <c>check_suite</c> or <c>push</c> belongs to (doc 21, Event mapping; doc 40 GitHub).
/// Those payloads carry no PR node id, so the PR is resolved from the read model, never from GitHub.
/// </summary>
public interface IGitHubPrLookup
{
    /// <summary>
    /// The open PR in <paramref name="repoNodeId"/> whose number is in <paramref name="numbers"/>, else the one whose stored head SHA
    /// is <paramref name="headSha"/>, else the one whose stored same-repository head branch is <paramref name="headRef"/> (a push to the
    /// PR branch before the stored head SHA is updated; PROPOSED). Null when none matches. With several matches the lowest PR number wins.
    /// </summary>
    Task<GitHubPrMatch?> FindOpenAsync(string repoNodeId, IReadOnlyList<int> numbers, string? headSha, string? headRef, CancellationToken cancellationToken);
}

/// <summary>The lookup over <c>github_pr</c>. Only registered PRs match; an unknown PR is simply not found.</summary>
public sealed class PgGitHubPrLookup(NpgsqlDataSource dataSource) : IGitHubPrLookup
{
    public async Task<GitHubPrMatch?> FindOpenAsync(string repoNodeId, IReadOnlyList<int> numbers, string? headSha, string? headRef, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoNodeId);
        ArgumentNullException.ThrowIfNull(numbers);

        if (numbers.Count > 0)
        {
            await using var byNumber = dataSource.CreateCommand(
                "SELECT pr_node_id, number, author_login FROM github_pr WHERE repo_node_id = @repo AND state = 'open' AND number = ANY(@numbers) ORDER BY number LIMIT 1");
            byNumber.Parameters.AddWithValue("repo", repoNodeId);
            byNumber.Parameters.AddWithValue("numbers", numbers.ToArray());
            if (await ReadAsync(byNumber, cancellationToken).ConfigureAwait(false) is { } found)
                return found;
        }

        if (!string.IsNullOrEmpty(headSha))
        {
            await using var bySha = dataSource.CreateCommand(
                "SELECT pr_node_id, number, author_login FROM github_pr WHERE repo_node_id = @repo AND state = 'open' AND head_sha = @sha ORDER BY number LIMIT 1");
            bySha.Parameters.AddWithValue("repo", repoNodeId);
            bySha.Parameters.AddWithValue("sha", headSha.ToLowerInvariant());
            if (await ReadAsync(bySha, cancellationToken).ConfigureAwait(false) is { } bySha1)
                return bySha1;
        }

        if (string.IsNullOrEmpty(headRef))
            return null;
        await using var byRef = dataSource.CreateCommand(
            "SELECT pr_node_id, number, author_login FROM github_pr WHERE repo_node_id = @repo AND state = 'open' AND head_ref = @ref ORDER BY number LIMIT 1");
        byRef.Parameters.AddWithValue("repo", repoNodeId);
        byRef.Parameters.AddWithValue("ref", headRef);
        return await ReadAsync(byRef, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<GitHubPrMatch?> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new GitHubPrMatch(reader.GetString(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : null;
    }
}

/// <summary>Fills the PR node id of a PR-less event from an <see cref="IGitHubPrLookup"/>. Pure apart from the lookup call.</summary>
public static class GitHubPrResolver
{
    /// <summary>
    /// For <c>check_run</c>, <c>check_suite</c> and <c>push</c> without a PR node id: returns the event with <see cref="GitHubEvent.PrNodeId"/>
    /// and, when missing, <see cref="GitHubEvent.PrAuthorLogin"/> set from the matching open PR. Any other event, an event that already
    /// names a PR, or one with no match is returned unchanged, so an unknown PR produces no effect and no error.
    /// </summary>
    public static async Task<GitHubEvent> ResolveAsync(IGitHubPrLookup? lookup, GitHubEvent gitHubEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gitHubEvent);
        if (lookup is null || gitHubEvent.PrNodeId is not null || gitHubEvent.EventName is not ("check_run" or "check_suite" or "push"))
            return gitHubEvent;

        var match = await lookup.FindOpenAsync(gitHubEvent.RepoNodeId, gitHubEvent.PullRequestNumbers, gitHubEvent.HeadSha, gitHubEvent.HeadRef, cancellationToken).ConfigureAwait(false);
        return match is null
            ? gitHubEvent
            : gitHubEvent with { PrNodeId = match.PrNodeId, PrAuthorLogin = gitHubEvent.PrAuthorLogin ?? match.AuthorLogin };
    }
}

/// <summary>Used when there is no database: nothing is registered, so nothing matches.</summary>
public sealed class NoGitHubPrLookup : IGitHubPrLookup
{
    public Task<GitHubPrMatch?> FindOpenAsync(string repoNodeId, IReadOnlyList<int> numbers, string? headSha, string? headRef, CancellationToken cancellationToken) =>
        Task.FromResult<GitHubPrMatch?>(null);
}
