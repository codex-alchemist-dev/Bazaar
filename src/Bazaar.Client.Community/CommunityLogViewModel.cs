using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community;

/// <summary>Fetches pages of the community log from the server (doc 36 work package 9). The head supplies it.</summary>
public interface ICommunityLogSource
{
    /// <summary>Returns one page, newest first. Throws when the server refuses or cannot be reached.</summary>
    Task<CommunityLogPageDto> LoadAsync(CommunityLogQueryDto query, CancellationToken cancellationToken);
}

/// <summary>One log line as shown. It has no body, only the body's hash.</summary>
public sealed class CommunityLogRowViewModel(CommunityLogRowDto row)
{
    public long Id => row.Id;

    public string AgentId => row.AgentId;

    public string Platform => row.Platform;

    public string Action => row.Action;

    public string Target => row.TargetUrl ?? "(no target)";

    /// <summary>The status in words, so it never relies on colour.</summary>
    public string Status => row.Status;

    /// <summary>Time in UTC, fixed format.</summary>
    public string Time => row.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    /// <summary>Full body hash, or a note that the action had no body.</summary>
    public string BodyHash => row.BodySha256 ?? "no body";

    public string Approval => row.ApprovalId ?? "none";

    public string ExternalId => row.ExternalId ?? "none";

    /// <summary>One line for the list and for screen readers.</summary>
    public string Summary => $"{Time}: {AgentId} {Action} on {Platform}, {Status}";
}

/// <summary>
/// The community log (doc 36 work package 9, doc 23): rows newest first, filtered by agent and platform, with load-more by
/// cursor. It never holds body text.
/// </summary>
public sealed class CommunityLogViewModel : INotifyPropertyChanged
{
    /// <summary>Default page size.</summary>
    public const int DefaultPageSize = 50;

    private readonly ICommunityLogSource _source;
    private readonly int _pageSize;
    private string _agentFilter = string.Empty;
    private string _platformFilter = string.Empty;
    private string? _nextCursor;
    private string? _error;
    private bool _isLoading;
    private bool _loaded;
    private int _generation;

    public CommunityLogViewModel(ICommunityLogSource source, int pageSize = DefaultPageSize)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _pageSize = pageSize > 0 ? pageSize : DefaultPageSize;
        RefreshCommand = new DraftCommand(RefreshAsync, () => true);
        LoadMoreCommand = new DraftCommand(LoadMoreAsync, () => CanLoadMore);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CommunityLogRowViewModel> Rows { get; } = [];

    public DraftCommand RefreshCommand { get; }

    public DraftCommand LoadMoreCommand { get; }

    /// <summary>Agent to filter by; blank means all agents. Takes effect on the next refresh.</summary>
    public string AgentFilter
    {
        get => _agentFilter;
        set => Set(ref _agentFilter, value ?? string.Empty, nameof(AgentFilter));
    }

    /// <summary>Platform to filter by; blank means all platforms. Takes effect on the next refresh.</summary>
    public string PlatformFilter
    {
        get => _platformFilter;
        set => Set(ref _platformFilter, value ?? string.Empty, nameof(PlatformFilter));
    }

    public bool IsLoading => _isLoading;

    public string? Error => _error;

    public bool HasError => _error is not null;

    public bool CanLoadMore => !_isLoading && _nextCursor is not null;

    /// <summary>True once a load has finished with no rows and no error.</summary>
    public bool IsEmpty => _loaded && _error is null && Rows.Count == 0;

    public string StatusText =>
        _isLoading ? "Loading the community log."
        : _error is not null ? "The community log could not be loaded."
        : !_loaded ? "The community log has not been loaded."
        : Rows.Count == 0 ? "No community actions match."
        : _nextCursor is null ? $"{Rows.Count} actions, newest first. This is the end of the log."
        : $"{Rows.Count} actions, newest first. More are available.";

    /// <summary>Reloads from the first page with the current filters.</summary>
    public Task RefreshAsync() => LoadAsync(append: false);

    /// <summary>Loads the next, older page and appends it.</summary>
    public Task LoadMoreAsync() => _nextCursor is null ? Task.CompletedTask : LoadAsync(append: true);

    private async Task LoadAsync(bool append)
    {
        var generation = ++_generation;
        var cursor = append ? _nextCursor : null;
        SetLoading(true);
        try
        {
            var query = new CommunityLogQueryDto(NullIfBlank(_agentFilter), NullIfBlank(_platformFilter), _pageSize, cursor);
            var page = await _source.LoadAsync(query, CancellationToken.None).ConfigureAwait(true);
            if (generation != _generation)
            {
                return; // A newer load started; this answer is stale.
            }

            if (!append)
            {
                Rows.Clear();
            }

            foreach (var row in page.Rows ?? [])
            {
                Rows.Add(new CommunityLogRowViewModel(row));
            }

            _nextCursor = string.IsNullOrEmpty(page.NextCursor) ? null : page.NextCursor;
            _error = null;
            _loaded = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (generation != _generation)
            {
                return;
            }

            _error = ex.Message;
            if (!append)
            {
                Rows.Clear();
                _nextCursor = null;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                SetLoading(false);
            }
        }
    }

    private void SetLoading(bool value)
    {
        _isLoading = value;
        foreach (var name in new[] { nameof(IsLoading), nameof(Error), nameof(HasError), nameof(CanLoadMore), nameof(IsEmpty), nameof(StatusText) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        LoadMoreCommand.RaiseCanExecuteChanged();
    }

    private void Set(ref string field, string value, string name)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
