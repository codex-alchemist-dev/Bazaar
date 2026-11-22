using System.Globalization;

namespace Bazaar.Server.Community.GitHub;

/// <summary>In-memory <see cref="ICommunityGitHubApi"/> for tests and offline hosts. Records every created item.</summary>
public sealed class FakeCommunityGitHubApi : ICommunityGitHubApi
{
    private readonly Lock _gate = new();
    private readonly List<FakeItem> _items = [];
    private long _nextId = 1000;

    /// <summary>One stored item.</summary>
    public sealed record FakeItem(
        string Kind, string Owner, string Repo, int? IssueNumber, string Title, string Body, string? Head, string? Base, CommunityGitHubItem Item, bool Draft);

    /// <summary>When set, the next call throws this exception once.</summary>
    public CommunityPostException? NextFailure { get; set; }

    public IReadOnlyList<FakeItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    public Task<CommunityGitHubItem> CreateIssueAsync(string owner, string repo, string title, string body, CancellationToken cancellationToken) =>
        Add("issue", owner, repo, null, title, body, null, null, draft: false);

    public Task<CommunityGitHubItem> CreateCommentAsync(string owner, string repo, int issueNumber, string body, CancellationToken cancellationToken) =>
        Add("comment", owner, repo, issueNumber, string.Empty, body, null, null, draft: false);

    public Task<CommunityGitHubItem?> FindByMarkerAsync(string owner, string repo, int? issueNumber, string marker, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfFailing();
            var found = _items.FirstOrDefault(i =>
                i.Owner == owner && i.Repo == repo && i.Body.Contains(marker, StringComparison.Ordinal)
                && (issueNumber is null ? i.Kind != "comment" : i.Kind == "comment" && i.IssueNumber == issueNumber));
            return Task.FromResult(found?.Item);
        }
    }

    private Task<CommunityGitHubItem> Add(
        string kind, string owner, string repo, int? number, string title, string body, string? head, string? baseBranch, bool draft)
    {
        lock (_gate)
        {
            ThrowIfFailing();
            var id = _nextId++;
            var path = kind == "pr" ? "pull" : "issues";
            var url = new Uri($"https://github.com/{owner}/{repo}/{path}/{(number ?? (int)(id - 999)).ToString(CultureInfo.InvariantCulture)}");
            var item = new CommunityGitHubItem(id.ToString(CultureInfo.InvariantCulture), url);
            _items.Add(new FakeItem(kind, owner, repo, number, title, body, head, baseBranch, item, draft));
            return Task.FromResult(item);
        }
    }

    private void ThrowIfFailing()
    {
        if (NextFailure is { } failure)
        {
            NextFailure = null;
            throw failure;
        }
    }
}
