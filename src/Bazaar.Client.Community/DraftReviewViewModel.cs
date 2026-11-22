using System.ComponentModel;
using Bazaar.Contracts.Community;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Community;

/// <summary>The list of draft-review cards and the selected one (doc 36, work package 8).</summary>
public sealed class DraftReviewViewModel : INotifyPropertyChanged
{
    private DraftReviewItemViewModel? _selected;

    public DraftReviewViewModel(
        IEnumerable<DraftReviewCardDto> cards,
        IDecisionSigner signer,
        IDraftReviewDecisionSender sender,
        TimeProvider? time = null,
        int skipped = 0)
    {
        ArgumentNullException.ThrowIfNull(cards);
        Cards = [.. cards.Select(card => new DraftReviewItemViewModel(card, signer, sender, time))];
        Skipped = skipped;
        _selected = Cards.Count > 0 ? Cards[0] : null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<DraftReviewItemViewModel> Cards { get; }

    /// <summary>Entries in the pane model that could not be read and were left out.</summary>
    public int Skipped { get; }

    public bool IsEmpty => Cards.Count == 0;

    public DraftReviewItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value))
            {
                return;
            }

            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    /// <summary>Builds the list from a pane descriptor. Throws <see cref="FormatException"/> for a model of the wrong shape.</summary>
    public static DraftReviewViewModel FromDescriptor(
        PaneDescriptor descriptor,
        IDecisionSigner signer,
        IDraftReviewDecisionSender sender,
        TimeProvider? time = null)
    {
        var parsed = DraftReviewCardMapper.Parse(descriptor);
        return new DraftReviewViewModel(parsed.Cards, signer, sender, time, parsed.Skipped);
    }

    public void SelectNext() => Move(1);

    public void SelectPrevious() => Move(-1);

    private void Move(int step)
    {
        if (Cards.Count == 0)
        {
            return;
        }

        var index = _selected is null ? -1 : IndexOf(_selected);
        Selected = Cards[Math.Clamp(index + step, 0, Cards.Count - 1)];
    }

    private int IndexOf(DraftReviewItemViewModel item)
    {
        for (var i = 0; i < Cards.Count; i++)
        {
            if (ReferenceEquals(Cards[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
