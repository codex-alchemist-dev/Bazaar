using System.ComponentModel;
using System.Globalization;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals.Avalonia;

/// <summary>One taint excerpt as the card shows it: always inside a quote block, never as an instruction.</summary>
/// <param name="Label">Where the text came from, for example the trust tag.</param>
/// <param name="Text">The quoted text, or a note that none is available.</param>
public sealed record QuotedExcerptItem(string Label, string Text);

/// <summary>
/// The approval card's view model (doc 29 Approvals). Approve and Deny are the only paths to a decision, and both go
/// through <see cref="ApprovalGate"/>, which asks for the biometric. Dictated text can fill a note for review and nothing
/// else: it never reaches the gate.
/// </summary>
public sealed class ApprovalCardViewModel : INotifyPropertyChanged
{
    private readonly ApprovalGate _gate;
    private ApprovalCardDto _card;
    private string _notice = "";
    private string _dictatedNote = "";
    private bool _isBusy;
    private bool _isInvalidated;
    private bool _isDecided;

    public ApprovalCardViewModel(ApprovalGate gate, ApprovalCardDto card)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _card = card ?? throw new ArgumentNullException(nameof(card));
        ApproveCommand = new ApprovalCommand(() => _ = DecideAsync(ApprovalDecisionKind.ApproveOnce), () => CanApprove);
        DenyCommand = new ApprovalCommand(() => _ = DecideAsync(ApprovalDecisionKind.Deny), () => CanDeny);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ApprovalCardDto Card => _card;

    public ApprovalCommand ApproveCommand { get; }

    public ApprovalCommand DenyCommand { get; }

    public string Agent => _card.AgentId;

    public string Tool => _card.Tool;

    public string Target => _card.Target ?? "(no target)";

    public string Summary => _card.Summary;

    public string PayloadText => _card.PayloadText;

    public string PayloadHash => _card.PayloadHash;

    public string ExpiresText => $"Expires {_card.ExpiresAt.ToString("u", CultureInfo.InvariantCulture)}";

    public string StatusText => _isInvalidated ? "Changed" : _card.Status.ToString();

    public IReadOnlyList<QuotedExcerptItem> Excerpts =>
        [.. _card.QuotedExcerpts.Select(e => new QuotedExcerptItem($"Quoted, {e.Trust}", e.Text ?? "(text not available)"))];

    public bool HasExcerpts => _card.QuotedExcerpts.Count > 0;

    /// <summary>Feedback for the last action. Empty when there is none.</summary>
    public string Notice
    {
        get => _notice;
        private set => Set(ref _notice, value, nameof(Notice), nameof(HasNotice));
    }

    public bool HasNotice => _notice.Length > 0;

    /// <summary>True while a biometric prompt or a send is in progress. Both buttons are off.</summary>
    public bool IsBusy => _isBusy;

    /// <summary>True when the card changed after it was shown. It cannot be decided; the owner must open the new request.</summary>
    public bool IsInvalidated => _isInvalidated;

    public bool CanApprove => CanDecide(ApprovalDecisionKind.ApproveOnce);

    public bool CanDeny => CanDecide(ApprovalDecisionKind.Deny);

    /// <summary>Text from dictation, kept for review. It is never a decision and never sent from here.</summary>
    public string DictatedNote => _dictatedNote;

    /// <summary>
    /// Receives dictated text. It only fills the review note: no biometric prompt, no signature and no decision, whatever
    /// the words are ("approve", "yes", "deny"). Doc 29 Question 2.
    /// </summary>
    public void ReceiveDictation(string text)
    {
        Set(ref _dictatedNote, text ?? "", nameof(DictatedNote));
    }

    /// <summary>
    /// Replaces the card with a newer copy of the same approval. A changed payload hash invalidates the card (doc 29
    /// Approvals): nothing on it can be decided any more. A status change alone just updates the display.
    /// </summary>
    public void UpdateCard(ApprovalCardDto next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.ApprovalId != _card.ApprovalId)
        {
            throw new ArgumentException("The card is for a different approval.", nameof(next));
        }

        if (!string.Equals(next.PayloadHash, _card.PayloadHash, StringComparison.Ordinal))
        {
            _isInvalidated = true;
            Notice = "This request changed after you opened it. Open the new request to review it.";
        }

        _card = next;
        RaiseAll();
    }

    /// <summary>Runs one decision through the gate and shows the outcome. Used by the buttons.</summary>
    public async Task DecideAsync(ApprovalDecisionKind kind)
    {
        if (!CanDecide(kind))
        {
            return;
        }

        _isBusy = true;
        RaiseAll();
        try
        {
            var result = await _gate.DecideAsync(new ApprovalDecisionRequest(_card, kind, ApprovalInputSource.CardButton), CancellationToken.None);
            if (result.IsSent)
            {
                _isDecided = true;
                Notice = kind == ApprovalDecisionKind.ApproveOnce ? "Approved." : "Denied.";
            }
            else
            {
                Notice = Describe(result.Refusal);
                // A signed decision is final here even when the send failed.
                _isDecided = result.Refusal is GateRefusal.SendFailed or GateRefusal.AlreadyDecided;
            }
        }
        finally
        {
            _isBusy = false;
            RaiseAll();
        }
    }

    private bool CanDecide(ApprovalDecisionKind kind) =>
        !_isBusy && !_isInvalidated && !_isDecided
        && _card.Status == ApprovalCardStatus.Pending
        && _card.AvailableDecisions.Contains(kind);

    private static string Describe(GateRefusal? refusal) => refusal switch
    {
        GateRefusal.BiometricCancelled => "Cancelled. Nothing was sent.",
        GateRefusal.BiometricFailed => "Fingerprint or face check failed. Nothing was sent.",
        GateRefusal.BiometricUnavailable => "No biometric is set up on this phone. Nothing was sent.",
        GateRefusal.CardExpired => "This request has expired. Nothing was sent.",
        GateRefusal.AlreadyDecided => "This request was already decided on this phone.",
        GateRefusal.AlreadyInProgress => "Another decision for this request is in progress.",
        GateRefusal.SigningFailed => "The phone's approval key could not sign. Nothing was sent.",
        GateRefusal.SendFailed => "The decision was signed but could not be sent. It will not be resent; check the request.",
        _ => "This decision was refused. Nothing was sent.",
    };

    private void RaiseAll()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        ApproveCommand.RaiseCanExecuteChanged();
        DenyCommand.RaiseCanExecuteChanged();
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
