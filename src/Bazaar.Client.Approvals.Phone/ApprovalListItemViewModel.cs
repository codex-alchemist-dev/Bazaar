using System.ComponentModel;
using System.Globalization;
using Bazaar.Client.Approvals.Avalonia;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals.Phone;

/// <summary>
/// One pending approval as a card in the phone list: who asked, what for, when it expires, and Approve, Reject and
/// Detail. Approve and Reject are the full card's own commands, so the phone has one decision path (the biometric gate).
/// </summary>
public sealed class ApprovalListItemViewModel : INotifyPropertyChanged
{
    private readonly Action<ApprovalListItemViewModel> _openDetail;

    internal ApprovalListItemViewModel(ApprovalCardViewModel card, Action<ApprovalListItemViewModel> openDetail)
    {
        Card = card;
        _openDetail = openDetail;
        DetailCommand = new ApprovalCommand(() => _openDetail(this), () => true);
        card.PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The full card. The detail view shows it as is.</summary>
    public ApprovalCardViewModel Card { get; }

    public Guid ApprovalId => Card.Card.ApprovalId;

    public string Summary => Card.Summary;

    public string Agent => Card.Agent;

    public string Tool => Card.Tool;

    public string Target => Card.Target;

    public string StatusText => Card.StatusText;

    public string ExpiresText => Card.ExpiresText;

    /// <summary>True when the card carries quoted untrusted text, so the list can flag it.</summary>
    public bool HasExcerpts => Card.HasExcerpts;

    /// <summary>Feedback for the last decision on this card. Empty when there is none.</summary>
    public string Notice => Card.Notice;

    public bool HasNotice => Card.HasNotice;

    public ApprovalCommand ApproveCommand => Card.ApproveCommand;

    /// <summary>Reject is the card's Deny.</summary>
    public ApprovalCommand RejectCommand => Card.DenyCommand;

    public ApprovalCommand DetailCommand { get; }

    internal void Update(ApprovalCardDto next) => Card.UpdateCard(next);

    internal string Describe() => string.Create(CultureInfo.InvariantCulture, $"{Agent} {Tool} {Target}");
}
