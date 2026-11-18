using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Mail.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Panes;

/// <summary>
/// The mail mailbox pane (pane kind <see cref="PaneKinds.MailMailbox"/>, doc 22 WP12). Model:
/// <c>{ "messages": [ { "from", "subject", "receivedAt", "text", "links": [ "url", ... ] } ] }</c>. The text is plain
/// text. Any <c>html</c> field the server sends is ignored, so HTML never renders. Links are kept as text and are never
/// fetched or opened here. A missing required field raises <see cref="PaneModelException"/>.
/// </summary>
public sealed class MailMailboxPaneViewModel : IPaneViewModel
{
    public MailMailboxPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Mail · Mailbox";

        Messages = PaneValues.Array(descriptor.Model, "messages").EnumerateArray().Select(ReadMessage).ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The messages, newest as the server sent them.</summary>
    public IReadOnlyList<MailboxMessageRow> Messages { get; }

    private static MailboxMessageRow ReadMessage(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each mailbox message must be an object");
        }

        return new MailboxMessageRow(
            PaneValues.RequiredString(item, "from"),
            PaneValues.RequiredString(item, "subject"),
            MailPaneParsing.Date(item, "receivedAt"),
            PaneValues.RequiredString(item, "text"),
            MailPaneParsing.Strings(item, "links"));
    }
}
