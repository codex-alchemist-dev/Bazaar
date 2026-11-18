using Bazaar.Client.Core.Panes;
using Bazaar.Client.Mail.Panes;

namespace Bazaar.Client.Mail;

/// <summary>
/// The mail client module: the pane modules a head composes to show mail (doc 22, WP12). Every pane is display-only. The
/// release, report and send decisions are made through Bazaar.Approvals, never from these panes.
/// </summary>
public static class MailPanes
{
    /// <summary>The quarantine, mailbox, approval card and DNS panes, in registration order.</summary>
    public static IReadOnlyList<IPaneModule> Modules { get; } =
    [
        new MailQuarantinePaneModule(),
        new MailMailboxPaneModule(),
        new MailApprovalPaneModule(),
        new MailDnsPaneModule(),
    ];
}
