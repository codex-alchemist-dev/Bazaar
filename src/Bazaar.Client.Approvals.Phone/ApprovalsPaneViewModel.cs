using System.Collections.ObjectModel;
using System.ComponentModel;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Approvals.Avalonia;
using Bazaar.Client.TalentPool;
using Bazaar.Client.TalentPool.Avalonia;
using Bazaar.Contracts.Approvals;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Approvals.Phone;

/// <summary>
/// The phone's approvals pane (doc 29 Approvals): the pending approvals as cards, each with Approve, Reject and Detail.
/// Detail opens the full card (exact payload, hash, quoted taint text). A card whose payload changed after it was shown
/// cannot be decided; the card says so (see <see cref="ApprovalCardViewModel.UpdateCard"/>). Nothing auto-approves.
/// </summary>
public sealed class ApprovalsPaneViewModel : IPaneViewModel, INotifyPropertyChanged
{
    public const string DefaultTitle = "Approvals";

    private readonly ApprovalGate? _gate;
    private readonly IPendingApprovalSource? _source;
    private readonly ToolGrantGate? _toolGrantGate;
    private readonly IPendingToolGrantSource? _toolGrantSource;
    private ApprovalCardViewModel? _detail;
    private string _notice = "";

    public ApprovalsPaneViewModel(
        PaneDescriptor descriptor,
        ApprovalGate? gate,
        IPendingApprovalSource? source,
        ToolGrantGate? toolGrantGate = null,
        IPendingToolGrantSource? toolGrantSource = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = string.IsNullOrEmpty(descriptor.Title) ? DefaultTitle : descriptor.Title;
        _gate = gate;
        _source = source;
        _toolGrantGate = toolGrantGate;
        _toolGrantSource = toolGrantSource;
        BackCommand = new ApprovalCommand(CloseDetail, () => _detail is not null);
        RefreshCommand = new ApprovalCommand(() => _ = RefreshAsync(), () => true);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Kind { get; }

    public string Title { get; }

    public ObservableCollection<ApprovalListItemViewModel> Items { get; } = [];

    /// <summary>Talent tool grants waiting for the owner (docs/design/25). Each has its own card with a biometric Approve and no deny.</summary>
    public ObservableCollection<ToolGrantCardViewModel> ToolGrants { get; } = [];

    public ApprovalCommand BackCommand { get; }

    public ApprovalCommand RefreshCommand { get; }

    /// <summary>The card open in detail, or null while the list shows.</summary>
    public ApprovalCardViewModel? Detail => _detail;

    public bool HasDetail => _detail is not null;

    public bool ShowList => _detail is null;

    public bool IsEmpty => Items.Count == 0 && ToolGrants.Count == 0;

    /// <summary>A note for the owner: nothing waiting, not connected, or no approval key. Empty when the list speaks for itself.</summary>
    public string Notice => _notice;

    public bool HasNotice => _notice.Length > 0;

    /// <summary>Fetches the pending approvals and brings the list in line. A card already shown is updated in place.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RefreshToolGrantsAsync(cancellationToken);
        if (_source is null)
        {
            SetNotice("This phone is not connected to a server, so it cannot list approvals.");
            return;
        }

        if (_gate is null)
        {
            SetNotice("This phone has no approval key set up, so it cannot decide approvals.");
            return;
        }

        IReadOnlyList<ApprovalCardDto> pending;
        try
        {
            pending = await _source.ListPendingAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetNotice("Could not load approvals. Nothing was changed.");
            return;
        }

        var wanted = pending.Where(c => c.Status == ApprovalCardStatus.Pending).ToList();
        foreach (var gone in Items.Where(i => wanted.All(c => c.ApprovalId != i.ApprovalId) && !i.Card.IsBusy).ToList())
        {
            Items.Remove(gone);
            if (ReferenceEquals(_detail, gone.Card))
            {
                CloseDetail();
            }
        }

        foreach (var card in wanted)
        {
            var existing = Items.FirstOrDefault(i => i.ApprovalId == card.ApprovalId);
            if (existing is null)
            {
                Items.Add(new ApprovalListItemViewModel(new ApprovalCardViewModel(_gate, card), OpenDetail));
            }
            else
            {
                existing.Update(card);
            }
        }

        SetNotice(Items.Count == 0 && ToolGrants.Count == 0 ? "Nothing is waiting for you." : "");
    }

    /// <summary>
    /// Brings the tool-grant cards in line with the server. Silent when the phone has no gate or source, or the fetch fails:
    /// the approval list still works, and no card is ever decided from stale text because a card is rebuilt only when its tools change.
    /// </summary>
    private async Task RefreshToolGrantsAsync(CancellationToken cancellationToken)
    {
        if (_toolGrantSource is null || _toolGrantGate is null)
        {
            return;
        }

        IReadOnlyList<ToolGrantCard> pending;
        try
        {
            pending = await _toolGrantSource.ListPendingAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        foreach (var gone in ToolGrants.Where(g => pending.All(c => c.HireKey != g.Card.HireKey) && g.CanApprove).ToList())
        {
            ToolGrants.Remove(gone);
        }

        foreach (var card in pending)
        {
            var existing = ToolGrants.FirstOrDefault(g => g.Card.HireKey == card.HireKey);
            if (existing is not null && (existing.Card.Tools.SequenceEqual(card.Tools) || !existing.CanApprove))
            {
                continue;
            }

            // A changed tool list is a new card: the old one can no longer be signed.
            if (existing is not null)
            {
                ToolGrants.Remove(existing);
            }

            ToolGrants.Add(new ToolGrantCardViewModel(_toolGrantGate, card));
        }

        Raise(nameof(IsEmpty));
    }

    private void OpenDetail(ApprovalListItemViewModel item)
    {
        _detail = item.Card;
        Raise(nameof(Detail), nameof(HasDetail), nameof(ShowList));
        BackCommand.RaiseCanExecuteChanged();
    }

    private void CloseDetail()
    {
        _detail = null;
        Raise(nameof(Detail), nameof(HasDetail), nameof(ShowList));
        BackCommand.RaiseCanExecuteChanged();
    }

    private void SetNotice(string text)
    {
        _notice = text;
        Raise(nameof(Notice), nameof(HasNotice), nameof(IsEmpty));
    }

    private void Raise(params string[] names)
    {
        foreach (var name in names)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
