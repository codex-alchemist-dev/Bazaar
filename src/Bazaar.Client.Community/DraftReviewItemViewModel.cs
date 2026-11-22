using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community;

/// <summary>What the owner has already done with a card in this session.</summary>
public enum DraftReviewOutcome
{
    /// <summary>No decision sent yet.</summary>
    None,

    /// <summary>An approval was sent.</summary>
    Approved,

    /// <summary>A denial was sent.</summary>
    Denied,

    /// <summary>An edited body was sent. It is a new draft and needs its own approval; the old approval is spent.</summary>
    EditSubmitted,
}

/// <summary>
/// One draft-review card (doc 36, work package 8). The body is quoted data, exposed as plain text only. Approval is
/// disabled when the disclosure footer is missing, a quality gate failed, the approval is no longer pending or has
/// expired, or the server did not offer that decision. Deny is never blocked by gates, and an edit is how a failed gate is fixed.
/// </summary>
public sealed class DraftReviewItemViewModel : INotifyPropertyChanged
{
    private readonly IDecisionSigner _signer;
    private readonly IDraftReviewDecisionSender _sender;
    private readonly TimeProvider _time;
    private bool _isBusy;
    private bool _isEditing;
    private string _editedBody;
    private string? _error;
    private DraftReviewOutcome _outcome;

    public DraftReviewItemViewModel(
        DraftReviewCardDto card,
        IDecisionSigner signer,
        IDraftReviewDecisionSender sender,
        TimeProvider? time = null)
    {
        Card = card ?? throw new ArgumentNullException(nameof(card));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _time = time ?? TimeProvider.System;
        _editedBody = card.Body;

        ApproveCommand = new DraftCommand(() => SendAsync(ApprovalDecisionKind.ApproveOnce, null, DraftReviewOutcome.Approved), () => CanApprove);
        DenyCommand = new DraftCommand(() => SendAsync(ApprovalDecisionKind.Deny, null, DraftReviewOutcome.Denied), () => CanDeny);
        EditCommand = new DraftCommand(BeginEdit, () => CanEdit);
        SubmitEditCommand = new DraftCommand(() => SendAsync(ApprovalDecisionKind.EditThenApprove, _editedBody, DraftReviewOutcome.EditSubmitted), () => CanSubmitEdit);
        CancelEditCommand = new DraftCommand(CancelEdit, () => _isEditing && !_isBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public DraftReviewCardDto Card { get; }

    public Guid DraftId => Card.DraftId;

    public string Platform => Card.Platform;

    public string Kind => Card.Kind;

    public string Target => Card.Target;

    public string? Title => Card.Title;

    public bool HasTitle => !string.IsNullOrEmpty(Card.Title);

    /// <summary>The exact outbound text. Show it inside a read-only quote block, never as markup.</summary>
    public string Body => Card.Body;

    public string BodySha256 => Card.BodySha256;

    public bool FooterPresent => Card.FooterPresent;

    public IReadOnlyList<QualityGateFindingDto> Gates => Card.Gates;

    public bool AnyGateFailed => Card.Gates.Any(g => !g.Passed);

    /// <summary>The taint lineage, each excerpt as plain quoted text with its trust tag.</summary>
    public IReadOnlyList<QuotedExcerptLine> Excerpts => [.. Card.Approval.QuotedExcerpts.Select(e => new QuotedExcerptLine(e.SourceId, e.Trust.ToString(), e.Text ?? "(text not available)"))];

    public bool HasExcerpts => Card.Approval.QuotedExcerpts.Count > 0;

    public DateTimeOffset ExpiresAt => Card.Approval.ExpiresAt;

    /// <summary>True when the approval's expiry has passed on the injected clock.</summary>
    public bool IsExpired => Card.Approval.Status == ApprovalCardStatus.Expired || _time.GetUtcNow() >= Card.Approval.ExpiresAt;

    public DraftReviewOutcome Outcome => _outcome;

    public bool IsBusy => _isBusy;

    public bool IsEditing => _isEditing;

    /// <summary>The text in the editor. Setting it never changes the approved body; the edit is sent as a new draft.</summary>
    public string EditedBody
    {
        get => _editedBody;
        set
        {
            if (string.Equals(_editedBody, value, StringComparison.Ordinal))
            {
                return;
            }

            _editedBody = value ?? string.Empty;
            Raise(nameof(EditedBody));
            Raise(nameof(EditedBodySha256));
            Raise(nameof(CanSubmitEdit));
            SubmitEditCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Hash of the edited text. It differs from <see cref="BodySha256"/> whenever the text changed, so the old approval cannot carry over.</summary>
    public string EditedBodySha256 => Hash(_editedBody);

    /// <summary>Why the last action failed, or null.</summary>
    public string? Error => _error;

    /// <summary>One line the view shows under the buttons.</summary>
    public string StatusText => _outcome switch
    {
        DraftReviewOutcome.Approved => "Approval sent.",
        DraftReviewOutcome.Denied => "Denial sent.",
        DraftReviewOutcome.EditSubmitted => "Edited text sent as a new draft. It is pending a new approval.",
        _ when IsExpired => "This approval has expired.",
        _ when Card.Approval.Status != ApprovalCardStatus.Pending => $"This approval is {Card.Approval.Status.ToString().ToLowerInvariant()}.",
        _ when !Card.FooterPresent => "Blocked: the AI-disclosure footer is missing.",
        _ when AnyGateFailed => "Blocked: a quality gate failed.",
        _ => string.Empty,
    };

    public DraftCommand ApproveCommand { get; }

    public DraftCommand DenyCommand { get; }

    /// <summary>Opens the editor.</summary>
    public DraftCommand EditCommand { get; }

    /// <summary>Sends the edited body as edit-then-approve.</summary>
    public DraftCommand SubmitEditCommand { get; }

    public DraftCommand CancelEditCommand { get; }

    private bool Open => _outcome == DraftReviewOutcome.None && !_isBusy && !IsExpired && Card.Approval.Status == ApprovalCardStatus.Pending;

    private bool Offers(ApprovalDecisionKind kind) => Card.Approval.AvailableDecisions.Contains(kind);

    /// <summary>
    /// True when the owner may approve this exact text: footer present, every gate passed, approval pending and not
    /// expired, the decision offered, and nothing sent or in flight.
    /// </summary>
    public bool CanApprove => Open && Card.FooterPresent && !AnyGateFailed && Offers(ApprovalDecisionKind.ApproveOnce);

    public bool CanDeny => Open && Offers(ApprovalDecisionKind.Deny);

    public bool CanEdit => Open && !_isEditing && Offers(ApprovalDecisionKind.EditThenApprove);

    /// <summary>An edit needs a non-empty body that is not the old text, so it becomes a new draft with a new hash.</summary>
    public bool CanSubmitEdit =>
        _isEditing
        && Open
        && Offers(ApprovalDecisionKind.EditThenApprove)
        && !string.IsNullOrWhiteSpace(_editedBody)
        && !string.Equals(_editedBody, Card.Body, StringComparison.Ordinal)
        && !string.Equals(EditedBodySha256, Card.BodySha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-evaluates time-dependent state (expiry). A head calls it from a timer or when the pane is shown.</summary>
    public void Refresh() => RaiseAll();

    private Task BeginEdit()
    {
        _isEditing = true;
        _editedBody = Card.Body;
        RaiseAll();
        return Task.CompletedTask;
    }

    private Task CancelEdit()
    {
        _isEditing = false;
        _editedBody = Card.Body;
        RaiseAll();
        return Task.CompletedTask;
    }

    private async Task SendAsync(ApprovalDecisionKind kind, string? editedBody, DraftReviewOutcome outcome)
    {
        _isBusy = true;
        _error = null;
        RaiseAll();
        try
        {
            var decision = await _signer.SignAsync(Card.Approval, kind, _time.GetUtcNow(), CancellationToken.None);
            if (decision is null || string.IsNullOrEmpty(decision.SignatureBase64))
            {
                _error = "The decision was not signed. Nothing was sent.";
                return;
            }

            if (decision.ApprovalId != Card.Approval.ApprovalId || decision.Decision != kind)
            {
                _error = "The signer returned a decision for a different approval. Nothing was sent.";
                return;
            }

            await _sender.SendAsync(new DraftReviewDecisionDto(Card.DraftId, decision, editedBody), CancellationToken.None);
            _outcome = outcome;
            _isEditing = false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _error = $"Could not send the decision: {ex.Message}";
        }
        finally
        {
            _isBusy = false;
            RaiseAll();
        }
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void RaiseAll()
    {
        foreach (var name in _changed)
        {
            Raise(name);
        }

        ApproveCommand.RaiseCanExecuteChanged();
        DenyCommand.RaiseCanExecuteChanged();
        EditCommand.RaiseCanExecuteChanged();
        SubmitEditCommand.RaiseCanExecuteChanged();
        CancelEditCommand.RaiseCanExecuteChanged();
    }

    private static readonly string[] _changed =
    [
        nameof(CanApprove), nameof(CanDeny), nameof(CanEdit), nameof(CanSubmitEdit), nameof(IsExpired), nameof(IsBusy),
        nameof(IsEditing), nameof(EditedBody), nameof(EditedBodySha256), nameof(Error), nameof(StatusText), nameof(Outcome),
    ];

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One taint excerpt as plain text for a quote block.</summary>
/// <param name="SourceId">Id of the event or message that carried it.</param>
/// <param name="Trust">Trust tag as text.</param>
/// <param name="Text">The excerpt, or a placeholder when the server sent none.</param>
public sealed record QuotedExcerptLine(string SourceId, string Trust, string Text);
