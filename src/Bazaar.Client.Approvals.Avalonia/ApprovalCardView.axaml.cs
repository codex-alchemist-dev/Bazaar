using Avalonia.Controls;

namespace Bazaar.Client.Approvals.Avalonia;

/// <summary>The approval card: agent, action, target, the exact payload and its hash, quoted taint excerpts, and Approve and Deny.</summary>
public sealed partial class ApprovalCardView : UserControl
{
    public ApprovalCardView() => InitializeComponent();
}
