using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Panes;

/// <summary>Registers the mail quarantine pane (<see cref="PaneKinds.MailQuarantine"/>).</summary>
public sealed class MailQuarantinePaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8). The pane is display-only.</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(MailQuarantinePaneModule), PaneKinds.MailQuarantine, [PaneKinds.MailQuarantine], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.MailQuarantine;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailQuarantine, descriptor => new MailQuarantinePaneViewModel(descriptor));
        registry.RegisterView<MailQuarantinePaneViewModel>(MailPaneViews.Quarantine);
    }
}

/// <summary>Registers the mail mailbox pane (<see cref="PaneKinds.MailMailbox"/>).</summary>
public sealed class MailMailboxPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8). The pane is display-only.</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(MailMailboxPaneModule), PaneKinds.MailMailbox, [PaneKinds.MailMailbox], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.MailMailbox;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailMailbox, descriptor => new MailMailboxPaneViewModel(descriptor));
        registry.RegisterView<MailMailboxPaneViewModel>(MailPaneViews.Mailbox);
    }
}

/// <summary>Registers the mail approval card (<see cref="PaneKinds.MailApproval"/>).</summary>
public sealed class MailApprovalPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8). The pane is display-only.</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(MailApprovalPaneModule), PaneKinds.MailApproval, [PaneKinds.MailApproval], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.MailApproval;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailApproval, descriptor => new MailApprovalPaneViewModel(descriptor));
        registry.RegisterView<MailApprovalPaneViewModel>(MailPaneViews.Approval);
    }
}

/// <summary>Registers the mail DNS pane (<see cref="PaneKinds.MailDns"/>).</summary>
public sealed class MailDnsPaneModule : IPaneModule, IClientModule
{
    /// <summary>The manifest the host checks at load (doc 41 section 8). The pane is display-only.</summary>
    public ModuleManifest Manifest { get; } = FirstPartyManifest.Create(typeof(MailDnsPaneModule), PaneKinds.MailDns, [PaneKinds.MailDns], ["ui:pane"]);

    /// <summary>Records the pane kinds this module registers, as its manifest declares them.</summary>
    public void Register(ClientModuleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        foreach (var kind in builder.Manifest.PaneKinds)
        {
            builder.AddPaneKind(kind);
        }
    }

    public string Id => PaneKinds.MailDns;

    public void Register(PaneRegistry registry, PaneModuleContext context)
    {
        registry.Register(PaneKinds.MailDns, descriptor => new MailDnsPaneViewModel(descriptor));
        registry.RegisterView<MailDnsPaneViewModel>(MailPaneViews.Dns);
    }
}
