using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.ViewModels;

/// <summary>
/// The PR list: one page at a time, a state filter, and a banner when the server answered from its cache (doc 52). The noun
/// ("pull request" or "merge request") comes from the provider UI when one is registered, and is neutral otherwise.
/// </summary>
public sealed class PrListViewModel : GitViewModelBase
{
    /// <summary>The state filters the list offers, in display order.</summary>
    public static IReadOnlyList<string> States { get; } = ["open", "closed", "merged", "all"];

    private readonly IGitGuiClient _client;
    private readonly int _pageSize;
    private IReadOnlyList<GitPrSummaryDto> _items = [];
    private string _state = "open";
    private int _page = 1;
    private bool _hasMore;
    private bool _stale;
    private bool _loading;
    private string? _error;

    public PrListViewModel(IGitGuiClient client, string provider, string repo, int pageSize, IGitProviderUi? providerUi = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        _client = client;
        Provider = provider;
        Repo = repo;
        _pageSize = pageSize;
        ProviderUi = providerUi;
    }

    public string Provider { get; }

    public string Repo { get; }

    public IGitProviderUi? ProviderUi { get; }

    /// <summary>What this provider calls a PR, or the neutral <c>pull request</c>.</summary>
    public string PrNoun => ProviderUi?.PrNoun ?? "pull request";

    public string Title => $"{Repo} · {PrNoun}s";

    public IReadOnlyList<GitPrSummaryDto> Items { get => _items; private set => Set(ref _items, value); }

    /// <summary>The state filter: one of <see cref="States"/>.</summary>
    public string State { get => _state; private set => Set(ref _state, value); }

    /// <summary>The current page, 1-based.</summary>
    public int Page { get => _page; private set => Set(ref _page, value); }

    public bool HasMore { get => _hasMore; private set => Set(ref _hasMore, value); }

    public bool HasPrevious => Page > 1;

    /// <summary>True when the forge was unreachable and the list is the server's last cached one.</summary>
    public bool Stale
    {
        get => _stale;
        private set
        {
            if (Set(ref _stale, value))
            {
                Raise(nameof(StaleBanner));
            }
        }
    }

    /// <summary>The banner text while <see cref="Stale"/>, otherwise null.</summary>
    public string? StaleBanner => Stale ? $"The server could not reach the provider. Showing the last saved list of {PrNoun}s; it may be out of date." : null;

    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    public Task LoadAsync(CancellationToken cancellationToken = default) => LoadPageAsync(Page, cancellationToken);

    public Task NextPageAsync(CancellationToken cancellationToken = default) =>
        HasMore ? LoadPageAsync(Page + 1, cancellationToken) : Task.CompletedTask;

    public Task PreviousPageAsync(CancellationToken cancellationToken = default) =>
        HasPrevious ? LoadPageAsync(Page - 1, cancellationToken) : Task.CompletedTask;

    /// <summary>Changes the state filter and reloads from page 1. An unknown state is ignored.</summary>
    public Task SetStateAsync(string state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var match = States.FirstOrDefault(s => string.Equals(s, state, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return Task.CompletedTask;
        }

        State = match;
        return LoadPageAsync(1, cancellationToken);
    }

    private async Task LoadPageAsync(int page, CancellationToken cancellationToken)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var result = await _client.ListPrsAsync(Provider, Repo, State, page, _pageSize, cancellationToken).ConfigureAwait(true);
            Items = result.Items;
            HasMore = result.HasMore;
            Stale = result.Stale;
            Page = page;
            Raise(nameof(HasPrevious));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the page already shown; the banner says why it did not change.
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
