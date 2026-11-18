using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Panes;

/// <summary>Registers the mail quarantine pane (<see cref="PaneKinds.MailQuarantine"/>).</summary>
public sealed class MailQuarantinePaneModule : IPaneModule
{
    public string Id => PaneKinds.MailQuarantine;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailQuarantine, descriptor => new MailQuarantinePaneViewModel(descriptor));
        registry.RegisterView<MailQuarantinePaneViewModel>(MailPaneViews.Quarantine);
    }
}

/// <summary>Registers the mail mailbox pane (<see cref="PaneKinds.MailMailbox"/>).</summary>
public sealed class MailMailboxPaneModule : IPaneModule
{
    public string Id => PaneKinds.MailMailbox;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailMailbox, descriptor => new MailMailboxPaneViewModel(descriptor));
        registry.RegisterView<MailMailboxPaneViewModel>(MailPaneViews.Mailbox);
    }
}

/// <summary>Registers the mail approval card (<see cref="PaneKinds.MailApproval"/>).</summary>
public sealed class MailApprovalPaneModule : IPaneModule
{
    public string Id => PaneKinds.MailApproval;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailApproval, descriptor => new MailApprovalPaneViewModel(descriptor));
        registry.RegisterView<MailApprovalPaneViewModel>(MailPaneViews.Approval);
    }
}

/// <summary>Registers the mail DNS pane (<see cref="PaneKinds.MailDns"/>).</summary>
public sealed class MailDnsPaneModule : IPaneModule
{
    public string Id => PaneKinds.MailDns;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailDns, descriptor => new MailDnsPaneViewModel(descriptor));
        registry.RegisterView<MailDnsPaneViewModel>(MailPaneViews.Dns);
    }
}
