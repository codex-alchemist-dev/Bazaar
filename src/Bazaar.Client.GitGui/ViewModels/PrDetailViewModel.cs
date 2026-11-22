using Bazaar.Client.GitGui.Diff;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.ViewModels;

/// <summary>
/// One PR: summary, body, files, checks, reviews and the merge card (doc 52). Files are read at a head sha; when the server
/// answers <c>head_moved</c> the model reloads at the new head and marks the old file selection stale instead of showing a diff
/// of a head the PR no longer has.
/// </summary>
public sealed class PrDetailViewModel : GitViewModelBase
{
    private const int _maxHeadRetries = 3;

    private readonly IGitGuiClient _client;
    private readonly int _diffContextLines;
    private GitPrDetailDto? _detail;
    private IReadOnlyList<GitPrFileDto> _files = [];
    private IReadOnlyList<GitCheckDto> _checks = [];
    private MergeCard? _merge;
    private GitPrFileDto? _selectedFile;
    private DiffModel? _selectedDiff;
    private string? _staleSelectionPath;
    private bool _loading;
    private string? _error;

    public PrDetailViewModel(IGitGuiClient client, string provider, string repo, long number, IGitProviderUi? providerUi = null, int diffContextLines = 3)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentOutOfRangeException.ThrowIfNegative(diffContextLines);

        _client = client;
        Provider = provider;
        Repo = repo;
        Number = number;
        ProviderUi = providerUi;
        _diffContextLines = diffContextLines;
    }

    /// <summary>Raised when the owner requests a merge from an enabled card. Nothing is signed or sent here.</summary>
    public event EventHandler<OwnerMergeRequestedEventArgs>? OwnerMergeRequested;

    public string Provider { get; }

    public string Repo { get; }

    public long Number { get; }

    public IGitProviderUi? ProviderUi { get; }

    public string PrNoun => ProviderUi?.PrNoun ?? "pull request";

    public string Title => _detail is { } d ? $"{PrNoun} #{Number} · {d.Summary.Title}" : $"{PrNoun} #{Number}";

    public GitPrDetailDto? Detail { get => _detail; private set { if (Set(ref _detail, value)) { Raise(nameof(Title)); } } }

    public string Body => _detail?.Body ?? string.Empty;

    public IReadOnlyList<GitPrFileDto> Files { get => _files; private set => Set(ref _files, value); }

    public IReadOnlyList<GitCheckDto> Checks { get => _checks; private set => Set(ref _checks, value); }

    public IReadOnlyList<GitReviewDto> Reviews => _detail?.Reviews ?? [];

    /// <summary>The merge card for the head currently shown, or null before the first load.</summary>
    public MergeCard? Merge { get => _merge; private set => Set(ref _merge, value); }

    public GitPrFileDto? SelectedFile { get => _selectedFile; private set => Set(ref _selectedFile, value); }

    /// <summary>The selected file's diff, with the context-lines setting applied.</summary>
    public DiffModel? SelectedDiff { get => _selectedDiff; private set => Set(ref _selectedDiff, value); }

    /// <summary>True when the head moved after a file was selected. The old selection is not shown; select a file again.</summary>
    public bool SelectionStale => _staleSelectionPath is not null;

    /// <summary>The path that was selected before the head moved, while <see cref="SelectionStale"/>.</summary>
    public string? StaleSelectionPath => _staleSelectionPath;

    public string? StaleNotice => SelectionStale
        ? $"The head changed, so your selection of {_staleSelectionPath} was cleared. The list below is the new head."
        : null;

    public bool IsLoading { get => _loading; private set => Set(ref _loading, value); }

    public string? Error { get => _error; private set => Set(ref _error, value); }

    /// <summary>Loads the PR, its files at its head, and its checks. Safe to call again as a refresh.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        Error = null;
        try
        {
            var previousHead = _detail?.Summary.HeadSha;
            var headMoved = false;
            GitPrDetailDto detail;
            IReadOnlyList<GitPrFileDto> files;
            var attempt = 0;
            while (true)
            {
                detail = await _client.GetPrAsync(Provider, Repo, Number, cancellationToken).ConfigureAwait(true);
                try
                {
                    files = await _client.GetFilesAsync(Provider, Repo, Number, detail.Summary.HeadSha, cancellationToken).ConfigureAwait(true);
                    break;
                }
                catch (HeadMovedException) when (++attempt < _maxHeadRetries)
                {
                    // The head moved between the two reads. Read the PR again and ask for the files at the new head.
                    headMoved = true;
                }
            }

            var checks = await _client.GetChecksAsync(Provider, Repo, Number, cancellationToken).ConfigureAwait(true);

            Detail = detail;
            Raise(nameof(Body));
            Raise(nameof(Reviews));
            Files = files;
            Checks = checks;
            Merge = MergeCard.Evaluate(detail.Summary, checks);

            var headChanged = headMoved || (previousHead is not null && !string.Equals(previousHead, detail.Summary.HeadSha, StringComparison.OrdinalIgnoreCase));
            if (headChanged && SelectedFile is { } old)
            {
                _staleSelectionPath = old.Path;
                SelectedFile = null;
                SelectedDiff = null;
                RaiseStale();
            }
            else if (SelectedFile is { } kept)
            {
                // Same head: refresh the selection from the new list.
                SelectFile(kept.Path);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Selects a file by path, clears the stale marker, and builds its diff. An unknown path selects nothing.</summary>
    public void SelectFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var file = _files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal));
        SelectedFile = file;
        SelectedDiff = file is null ? null : DiffParser.Parse(file).WithContext(_diffContextLines);
        if (_staleSelectionPath is not null)
        {
            _staleSelectionPath = null;
            RaiseStale();
        }
    }

    /// <summary>
    /// Asks for a merge. Returns false and raises nothing when the card is off. Otherwise raises
    /// <see cref="OwnerMergeRequested"/> with the provider, repo, number and the card's head sha.
    /// </summary>
    public bool RequestMerge()
    {
        if (Merge is not { CanRequestMerge: true } card)
        {
            return false;
        }

        OwnerMergeRequested?.Invoke(this, new OwnerMergeRequestedEventArgs(card.Provider, card.Repo, card.Number, card.HeadSha));
        return true;
    }

    private void RaiseStale()
    {
        Raise(nameof(SelectionStale));
        Raise(nameof(StaleSelectionPath));
        Raise(nameof(StaleNotice));
    }
}
