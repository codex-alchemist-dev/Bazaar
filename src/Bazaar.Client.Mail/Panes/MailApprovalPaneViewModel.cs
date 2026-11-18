using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Mail.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Panes;

/// <summary>
/// The mail approval card (pane kind <see cref="PaneKinds.MailApproval"/>, doc 22 WP12). Model:
/// <c>{ "approvalId", "from", "recipients": [ "address", ... ], "subject", "body", "footerPreview", "reason" }</c>. The card only shows
/// these. It has no release, approve or send action; those go through Bazaar.Approvals. A missing required field, or an
/// empty recipient list, raises <see cref="PaneModelException"/>.
/// </summary>
public sealed class MailApprovalPaneViewModel : IPaneViewModel
{
    public MailApprovalPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Mail · Approval";

        var model = descriptor.Model;
        var recipients = MailPaneParsing.Strings(model, "recipients");
        if (recipients.Count == 0)
        {
            throw new PaneModelException("an approval card needs at least one recipient");
        }

        Card = new MailApprovalCard(
            PaneValues.RequiredString(model, "approvalId"),
            PaneValues.RequiredString(model, "from"),
            recipients,
            PaneValues.RequiredString(model, "subject"),
            PaneValues.RequiredString(model, "body"),
            PaneValues.RequiredString(model, "footerPreview"),
            PaneValues.RequiredString(model, "reason"));
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The card the owner reads. Every string in it is untrusted text.</summary>
    public MailApprovalCard Card { get; }
}
