using Avalonia.Controls;
using Bazaar.Client.Community;

namespace Bazaar.Client.Community.Avalonia;

/// <summary>
/// The community log: filters, rows newest first with status in words, a load-more button, and empty and error states. It
/// shows the body hash, never body text. Head-neutral Avalonia.
/// </summary>
public sealed partial class CommunityLogView : UserControl
{
    public CommunityLogView() => InitializeComponent();

    public CommunityLogView(CommunityLogViewModel viewModel)
        : this() => DataContext = viewModel;
}
