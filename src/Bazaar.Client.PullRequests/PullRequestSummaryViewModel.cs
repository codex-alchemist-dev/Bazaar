using Bazaar.Client.Core.Wire;
using Bazaar.Contracts.GitHub;

namespace Bazaar.Client.PullRequests;

/// <summary>One line of a unified-diff hunk as the native diff surface draws it.</summary>
/// <param name="Kind">Header, added, removed or context.</param>
/// <param name="Text">The line, with its diff prefix.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>What a diff line is.</summary>
public enum DiffLineKind
{
    Header,
    Added,
    Removed,
    Context,
}

/// <summary>
/// The merge intent that is waiting for the owner's tap and biometric. It is not yet signed; the signature comes from
/// <see cref="IOwnerActionGate"/> and lives only for this one attempt.
/// </summary>
internal sealed record PendingMerge(OwnerMergeIntent Intent);

/// <summary>
/// One PR: summary, changed files, checks, the native diff (when <c>android.folded_full_diff</c> is on), request-changes and
/// comment reviews, and the owner-signed merge (when <c>android.merge_from_folded</c> is on). There is no approve command:
/// reviewer approval is shown as data only. A stale reply refreshes the PR and voids any pending merge.
/// </summary>
public sealed class PullRequestSummaryViewModel : PullRequestViewModelBase
{
    private readonly IPullRequestClient _client;
    private readonly IOwnerActionGate _gate;
    private readonly Core.Mobile.AndroidSettings _settings;
    private PrListRow _row;
    private PrSummaryReply? _summary;
    private PendingMerge? _pending;
    private IReadOnlyList<DiffLine> _diffLines = [];
    private string? _diffPath;
    private string _notice = "";
    private bool _isBusy;
    private bool _isMerged;

    /// <summary>Creates the view model for one PR row from the list.</summary>
    public PullRequestSummaryViewModel(
        PrListRow row,
        IPullRequestClient client,
        IOwnerActionGate gate,
        Core.Mobile.AndroidSettings settings)
    {
        _row = row ?? throw new ArgumentNullException(nameof(row));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Raised when a reply says the head moved, so a host can show that the PR changed.</summary>
    public event EventHandler<string>? HeadMoved;

    /// <summary>The PR id the wire names it by.</summary>
    public string PrId => _row.PrId;

    /// <summary>The PR title.</summary>
    public string Title => _row.Title;

    /// <summary>The head sha the screen describes. A merge is only ever for this sha.</summary>
    public string HeadSha => _summary?.HeadSha ?? _row.HeadSha;

    /// <summary>The PR number, for display.</summary>
    public int Number => _row.Number;

    /// <summary>The summary text, or empty until loaded.</summary>
    public string Summary => _summary?.Summary ?? "";

    /// <summary>Changed files at the head.</summary>
    public IReadOnlyList<PrChangedFile> Files => _summary?.Files ?? [];

    /// <summary>Checks rollup at the head.</summary>
    public PrChecksRollup Checks => _summary?.Checks ?? _row.Checks;

    /// <summary>Reviewer approval. Data only, never merge authority.</summary>
    public bool ReviewerApproved => _summary?.ReviewerApproved ?? false;

    /// <summary>True when the PR is a draft.</summary>
    public bool IsDraft => _row.Draft;

    /// <summary>The PR identity for the merge hash, or null when the server did not send it. Null means no merge.</summary>
    public PrIdentity? Identity => _summary is { } s ? PrIdentity.From(s) ?? PrIdentity.From(_row) : PrIdentity.From(_row);

    /// <summary>Lines of the last diff opened, or none.</summary>
    public IReadOnlyList<DiffLine> DiffLines => _diffLines;

    /// <summary>The path of the diff currently shown, or null.</summary>
    public string? DiffPath => _diffPath;

    /// <summary>True while a request is in flight.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>Feedback for the last action, or empty.</summary>
    public string Notice => _notice;

    /// <summary>True once the merge has happened.</summary>
    public bool IsMerged => _isMerged;

    /// <summary>True when the full diff is allowed on this phone (<c>android.folded_full_diff</c>, default off).</summary>
    public bool FullDiffEnabled => _settings.FoldedFullDiff;

    /// <summary>True when a merge is waiting for the owner's tap.</summary>
    public bool HasPendingMerge => _pending is not null;

    /// <summary>The waiting merge's commit title, or empty.</summary>
    public string PendingMergeTitle => _pending?.Intent.CommitTitle ?? "";

    /// <summary>Whether the merge control is enabled. It is off for drafts, failing checks, unknown identity, a busy screen, or the setting off.</summary>
    public bool CanMerge =>
        _settings.MergeFromFolded
        && Identity is not null
        && !IsDraft
        && Checks == PrChecksRollup.Passing
        && !IsBusy
        && !IsMerged;

    /// <summary>Whether a review can be sent. Reviews need the loaded head.</summary>
    public bool CanReview => _summary is not null && !IsBusy;

    /// <summary>Loads the summary at the current head. A changed head voids any pending merge.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _isBusy = true;
        Touch();
        try
        {
            var summary = await _client.SummaryAsync(PrId, cancellationToken).ConfigureAwait(false);
            ApplyHead(summary.HeadSha, summary.Checks);
            _summary = summary;
            _notice = "";
        }
        catch (WireRequestException error)
        {
            _notice = $"The server refused the summary: {error.Error.Code}.";
        }
        finally
        {
            _isBusy = false;
            Touch();
        }
    }

    /// <summary>Opens the waiting merge for the owner's confirmation. Nothing is signed yet.</summary>
    public void BeginMerge(string commitMessage = "", string? signOff = null)
    {
        if (!CanMerge || Identity is not { } identity)
        {
            _notice = _settings.MergeFromFolded ? "Merging is not available for this PR." : "Merging is off on this phone.";
            Touch();
            return;
        }

        var message = signOff is { Length: > 0 } ? $"{commitMessage}\n\n{signOff}".TrimStart() : commitMessage;
        _pending = new PendingMerge(new OwnerMergeIntent(OwnerActionSource.Button, identity, HeadSha, $"{Title} (#{Number})", message));
        _notice = "Confirm the merge. A fresh biometric check is needed.";
        Touch();
    }

    /// <summary>Drops the waiting merge without signing anything.</summary>
    public void CancelMerge()
    {
        _pending = null;
        _notice = "";
        Touch();
    }

    /// <summary>
    /// Runs the waiting merge: biometric, one signature, one send. <paramref name="source"/> must be a tap; dictation is refused by the gate.
    /// </summary>
    public async Task ConfirmMergeAsync(OwnerActionSource source, CancellationToken cancellationToken = default)
    {
        if (_pending is not { } pending)
        {
            return;
        }

        if (pending.Intent.HeadSha != HeadSha)
        {
            VoidPending("The head moved. Approve again.");
            return;
        }

        _isBusy = true;
        Touch();
        try
        {
            var intent = pending.Intent with { Source = source };
            var result = await _gate.AuthorizeMergeAsync(intent, cancellationToken).ConfigureAwait(false);
            if (result.Refusal is { } refusal)
            {
                _pending = null;
                _notice = RefusalText(refusal);
                return;
            }

            var signed = result.Approval!;
            if (!_gate.TrySpend(signed))
            {
                VoidPending("This approval was already used. Approve again.");
                return;
            }

            var reply = await _client.MergeAsync(
                new PrMergeRequest(PrId, signed.HeadSha, intent.CommitTitle, intent.CommitMessage, signed.Payload, signed.Signature),
                cancellationToken).ConfigureAwait(false);
            await ApplyMergeReplyAsync(reply, cancellationToken).ConfigureAwait(false);
        }
        catch (WireRequestException error)
        {
            VoidPending($"The server refused the merge: {error.Error.Code}.");
        }
        finally
        {
            _isBusy = false;
            Touch();
        }
    }

    /// <summary>Sends a request-changes review at the loaded head.</summary>
    public Task RequestChangesAsync(string body, CancellationToken cancellationToken = default) =>
        SendReviewAsync(PrReviewEvents.RequestChanges, body, cancellationToken);

    /// <summary>Sends a comment review at the loaded head.</summary>
    public Task CommentAsync(string body, CancellationToken cancellationToken = default) =>
        SendReviewAsync(PrReviewEvents.Comment, body, cancellationToken);

    /// <summary>Loads the hunks of one file at the loaded head. Off unless <c>android.folded_full_diff</c> is on.</summary>
    public async Task OpenDiffAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!_settings.FoldedFullDiff)
        {
            _notice = "The full diff is off on this phone.";
            Touch();
            return;
        }

        if (_summary is null)
        {
            return;
        }

        _isBusy = true;
        Touch();
        try
        {
            var reply = await _client.HunksAsync(new PrHunksRequest(PrId, HeadSha, path, null), cancellationToken).ConfigureAwait(false);
            if (reply.Status == PrHunksStatus.Stale)
            {
                await HandleStaleAsync(reply.HeadSha, cancellationToken).ConfigureAwait(false);
                return;
            }

            _diffPath = path;
            _diffLines = [.. reply.Hunks.SelectMany(FlattenHunk)];
        }
        catch (WireRequestException error)
        {
            _notice = $"The server refused the diff: {error.Error.Code}.";
        }
        finally
        {
            _isBusy = false;
            Touch();
        }
    }

    private async Task SendReviewAsync(string evt, string body, CancellationToken cancellationToken)
    {
        if (!CanReview)
        {
            return;
        }

        _isBusy = true;
        Touch();
        try
        {
            var reply = await _client.ReviewAsync(new PrReviewRequest(PrId, HeadSha, evt, body), cancellationToken).ConfigureAwait(false);
            switch (reply.Status)
            {
                case PrReviewStatus.Applied:
                    _notice = "Review sent.";
                    break;
                case PrReviewStatus.Stale:
                    await HandleStaleAsync(reply.HeadSha ?? HeadSha, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    _notice = reply.Message ?? "The review was refused.";
                    break;
            }
        }
        catch (WireRequestException error)
        {
            _notice = $"The server refused the review: {error.Error.Code}.";
        }
        finally
        {
            _isBusy = false;
            Touch();
        }
    }

    private async Task ApplyMergeReplyAsync(PrMergeReply reply, CancellationToken cancellationToken)
    {
        switch (reply.Status)
        {
            case PrMergeStatus.Merged:
                _pending = null;
                _isMerged = true;
                _notice = "Merged.";
                break;
            case PrMergeStatus.Stale:
                await HandleStaleAsync(reply.HeadSha ?? HeadSha, cancellationToken).ConfigureAwait(false);
                _notice = "The head moved. Approve again.";
                break;
            case PrMergeStatus.ChecksFailed:
                VoidPending(reply.Message ?? "Checks are not passing.");
                break;
            case PrMergeStatus.BadSignature:
                VoidPending("The owner signature was refused. Approve again.");
                break;
            default:
                VoidPending(reply.Message ?? "The merge was refused.");
                break;
        }
    }

    /// <summary>A stale reply: void the pending merge, refresh the summary, and tell the host the head moved.</summary>
    private async Task HandleStaleAsync(string currentHead, CancellationToken cancellationToken)
    {
        VoidPending("The head moved. Approve again.");
        HeadMoved?.Invoke(this, currentHead);
        _diffLines = [];
        _diffPath = null;
        var summary = await _client.SummaryAsync(PrId, cancellationToken).ConfigureAwait(false);
        ApplyHead(summary.HeadSha, summary.Checks);
        _summary = summary;
    }

    private void ApplyHead(string headSha, PrChecksRollup checks)
    {
        if (!string.Equals(headSha, HeadSha, StringComparison.Ordinal))
        {
            // A new head voids everything tied to the old one: the pending merge, the diff and the reviews' target.
            VoidPending("The head moved. Approve again.");
            _diffLines = [];
            _diffPath = null;
        }

        _row = _row with { HeadSha = headSha, Checks = checks };
    }

    private void VoidPending(string notice)
    {
        _pending = null;
        _notice = notice;
        Touch();
    }

    private static IEnumerable<DiffLine> FlattenHunk(PrHunk hunk)
    {
        yield return new DiffLine(DiffLineKind.Header, hunk.Header);
        foreach (var line in hunk.Lines)
        {
            yield return line switch
            {
                { Length: > 0 } when line[0] == '+' => new DiffLine(DiffLineKind.Added, line),
                { Length: > 0 } when line[0] == '-' => new DiffLine(DiffLineKind.Removed, line),
                _ => new DiffLine(DiffLineKind.Context, line),
            };
        }
    }

    private static string RefusalText(OwnerActionRefusal refusal) => refusal switch
    {
        OwnerActionRefusal.NotFromButton => "Merge needs a tap on the merge control.",
        OwnerActionRefusal.MergeDisabled => "Merging is off on this phone.",
        OwnerActionRefusal.InvalidRequest => "This PR cannot be merged from the phone.",
        OwnerActionRefusal.BiometricCancelled => "Merge cancelled.",
        OwnerActionRefusal.BiometricUnavailable => "No biometric is set up on this device, so the merge is blocked.",
        OwnerActionRefusal.BiometricExpired => "The biometric check ran out. Approve again.",
        OwnerActionRefusal.SigningFailed => "The device could not sign the merge.",
        _ => "The biometric check failed. The merge is blocked.",
    };
}
