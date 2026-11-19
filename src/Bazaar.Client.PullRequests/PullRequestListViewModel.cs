using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.GitHub;

namespace Bazaar.Client.PullRequests;

/// <summary>The PR list (doc 29, Review a PR and merge it). It shows server-proxied rows and pages on request; it never merges.</summary>
public sealed class PullRequestListViewModel(IPullRequestClient client) : PullRequestViewModelBase
{
    /// <summary>Rows per page. The server clamps it.</summary>
    public const int PageSize = 20;

    private IReadOnlyList<PrListRow> _rows = [];
    private string? _nextCursor;
    private string _notice = "";
    private bool _isBusy;
    private PrListState _state = PrListState.Open;
    private string? _repo;

    /// <summary>The rows loaded so far.</summary>
    public IReadOnlyList<PrListRow> Rows => _rows;

    /// <summary>Which PRs are listed.</summary>
    public PrListState State => _state;

    /// <summary>Optional <c>owner/repo</c> filter; null lists every watched repo.</summary>
    public string? Repo => _repo;

    /// <summary>True when another page is available.</summary>
    public bool HasMore => _nextCursor is not null;

    /// <summary>Feedback for the last action, or empty.</summary>
    public string Notice => _notice;

    /// <summary>True while a request is in flight.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>Loads the first page for a state and filter, replacing the rows.</summary>
    public Task RefreshAsync(PrListState state = PrListState.Open, string? repo = null, CancellationToken cancellationToken = default)
    {
        _state = state;
        _repo = repo;
        return LoadAsync(cursor: null, append: false, cancellationToken);
    }

    /// <summary>Loads the next page, appending to the rows.</summary>
    public Task LoadMoreAsync(CancellationToken cancellationToken = default) =>
        _nextCursor is null ? Task.CompletedTask : LoadAsync(_nextCursor, append: true, cancellationToken);

    private async Task LoadAsync(string? cursor, bool append, CancellationToken cancellationToken)
    {
        _isBusy = true;
        Touch();
        try
        {
            var reply = await client.ListAsync(new PrListRequest(_repo, _state, cursor, PageSize), cancellationToken).ConfigureAwait(false);
            _rows = append ? [.. _rows, .. reply.Rows] : reply.Rows;
            _nextCursor = reply.NextCursor;
            _notice = "";
        }
        catch (WireRequestException error)
        {
            _notice = $"The server refused the list: {error.Error.Code}.";
        }
        finally
        {
            _isBusy = false;
            Touch();
        }
    }
}
