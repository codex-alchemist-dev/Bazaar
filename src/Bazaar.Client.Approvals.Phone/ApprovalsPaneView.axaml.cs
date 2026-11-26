using Avalonia.Controls;

namespace Bazaar.Client.Approvals.Phone;

/// <summary>The phone approvals pane body: the pending list, or one card in detail.</summary>
public sealed partial class ApprovalsPaneView : UserControl
{
    public ApprovalsPaneView() => InitializeComponent();

    public ApprovalsPaneView(ApprovalsPaneViewModel viewModel)
        : this() => DataContext = viewModel;
}
