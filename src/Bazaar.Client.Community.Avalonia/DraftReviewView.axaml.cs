using Avalonia.Controls;
using Bazaar.Client.Community;

namespace Bazaar.Client.Community.Avalonia;

/// <summary>
/// The draft-review card: where the post goes, the exact body in a read-only quote, its hash, the gate results, the
/// disclosure-footer indicator, the quoted taint excerpts, and Approve, Deny and Edit buttons. Head-neutral Avalonia.
/// </summary>
public sealed partial class DraftReviewView : UserControl
{
    public DraftReviewView() => InitializeComponent();

    public DraftReviewView(DraftReviewViewModel viewModel)
        : this() => DataContext = viewModel;
}
