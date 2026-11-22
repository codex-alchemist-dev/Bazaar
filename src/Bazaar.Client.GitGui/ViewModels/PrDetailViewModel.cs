using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.Merge;
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
    private readonly IOwnerMergeApprover? _approver;
    private MergeCardState _mergeState;
    private string? _mergeMessage;
    private GitPrDetailDto? _detail;
    private IReadOnlyList<GitPrFileDto> _files = [];
    private IReadOnlyList<GitCheckDto> _checks = [];
    private MergeCard? _merge;
    private GitPrFileDto? _selectedFile;
    private DiffModel? _selectedDiff;
    private string? _staleSelectionPath;
    private bool _loading;
    private string? _error;

    public PrDetailViewModel(IGitGuiClient client, string provider, string repo, long number, IGitProviderUi? providerUi = null, int diffContextLines = 3, IOwnerMergeApprover? mergeApprover = null)
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
        _approver = mergeApprover;
    }

    /// <summary>Raised when the owner requests a merge from an enabled card. The event itself signs and sends nothing; <see cref="MergeAsync"/> does.</summary>
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
    public MergeCard? Merge { get => _merge; private set { if (Set(ref _merge, value)) { Raise(nameof(CanMerge)); } } }

    /// <summary>Where the last merge attempt stands.</summary>
    public MergeCardState MergeState
    {
        get => _mergeState;
        private set
        {
            if (Set(ref _mergeState, value))
            {
                Raise(nameof(CanMerge));
            }
        }
    }

    /// <summary>The line to show for <see cref="MergeState"/>, or null when idle. Never carries server text or a signature.</summary>
    public string? MergeMessage { get => _mergeMessage; private set => Set(ref _mergeMessage, value); }

    /// <summary>True when the merge button is on: the card allows it and no attempt is running or finished.</summary>
    public bool CanMerge => Merge is { CanRequestMerge: true } && _mergeState is not (MergeCardState.Signing or MergeCardState.Merging or MergeCardState.Merged);

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

    /// <summary>
    /// The merge button (doc 52 Client). Builds the owner approval for the card's head, asks the device approver for a fresh
    /// presence check and signature, and posts it to the server's merge route. The client holds no forge credential and calls no
    /// forge. Outcomes map to <see cref="MergeState"/>: 409 reloads at the new head and says to review again; 403 is a generic
    /// "not approved"; 429 and 5xx say to try later. Returns true only when the server merged.
    /// </summary>
    public async Task<bool> MergeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanMerge || Merge is not { } card || _detail is not { } detail)
        {
            return false;
        }

        RequestMerge();
        if (_approver is null)
        {
            SetMerge(MergeCardState.CannotApprove, "This client cannot approve merges.");
            return false;
        }

        SetMerge(MergeCardState.Signing, "Waiting for your approval.");
        try
        {
            var title = DeviceOwnerMergeApprover.CommitTitleFor(detail.Summary.Title, card.Number);
            var approval = await _approver.ApproveAsync(
                new MergeApprovalRequest(card.Provider, card.Repo, card.Number, card.HeadSha, title), cancellationToken).ConfigureAwait(true);
            if (approval.Outcome != MergeApprovalOutcome.Approved || approval.Merge is not { } request)
            {
                var (state, text) = approval.Outcome switch
                {
                    MergeApprovalOutcome.Cancelled => (MergeCardState.Cancelled, "Approval cancelled. Nothing was merged."),
                    MergeApprovalOutcome.Unavailable => (MergeCardState.CannotApprove, "This device cannot approve merges."),
                    _ => (MergeCardState.Failed, "The approval could not be signed. Nothing was merged."),
                };
                SetMerge(state, text);
                return false;
            }

            SetMerge(MergeCardState.Merging, "Merging.");
            await _client.MergeAsync(card.Provider, card.Repo, card.Number, request, cancellationToken).ConfigureAwait(true);
            SetMerge(MergeCardState.Merged, $"Merged at head {card.HeadSha}.");
            await LoadAsync(cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetMerge(MergeCardState.Cancelled, "Approval cancelled. Nothing was merged.");
            return false;
        }
        catch (HeadMovedException)
        {
            await LoadAsync(cancellationToken).ConfigureAwait(true);
            SetMerge(MergeCardState.HeadMoved, "The head moved. Review the new head and approve again.");
            return false;
        }
        catch (GitMergeNotApprovedException)
        {
            SetMerge(MergeCardState.NotApproved, "Not approved.");
            return false;
        }
        catch (GitRateLimitedException limited)
        {
            SetMerge(MergeCardState.RateLimited, limited.RetryAfterSeconds is { } s
                ? $"The forge is busy. Try again in {s} seconds."
                : "The forge is busy. Try again later.");
            return false;
        }
        catch (GitServerUnavailableException)
        {
            SetMerge(MergeCardState.Unavailable, "The server or the forge is unavailable. The merge may not have happened; check before trying again.");
            return false;
        }
        catch (GitGuiException)
        {
            SetMerge(MergeCardState.Failed, "The merge could not be completed.");
            return false;
        }
    }

    private void SetMerge(MergeCardState state, string? message)
    {
        MergeState = state;
        MergeMessage = message;
    }

    private void RaiseStale()
    {
        Raise(nameof(SelectionStale));
        Raise(nameof(StaleSelectionPath));
        Raise(nameof(StaleNotice));
    }
}
