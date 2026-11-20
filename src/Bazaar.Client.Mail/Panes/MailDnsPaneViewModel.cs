using System.Text.Json;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Client.Mail.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Panes;

/// <summary>
/// The mail DNS pane (pane kind <see cref="PaneKinds.MailDns"/>, doc 22 WP10 and WP12). Model:
/// <c>{ "domain": "name or null", "rows": [ { "kind", "status", "detail", "fix": "text or null" } ] }</c>. Kinds are
/// <c>Spf</c>, <c>Dkim</c>, <c>Dmarc</c>, <c>Ptr</c> and <c>BulkSender</c>. Statuses are <c>Pass</c>, <c>Warn</c>,
/// <c>Fail</c> and <c>NotApplicable</c>. Those are the server's <c>MailDnsReport</c> names, so a report maps to a row
/// without a server reference. An unknown kind or status raises <see cref="PaneModelException"/>.
/// </summary>
public sealed class MailDnsPaneViewModel : IPaneViewModel
{
    public MailDnsPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Mail · DNS";

        Domain = MailPaneParsing.OptionalString(descriptor.Model, "domain");
        Rows = PaneValues.Array(descriptor.Model, "rows").EnumerateArray().Select(ReadRow).ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The operator's domain, when the server names it. Untrusted text.</summary>
    public string? Domain { get; }

    /// <summary>The check rows, in the order the server sent them.</summary>
    public IReadOnlyList<MailDnsRow> Rows { get; }

    private static MailDnsRow ReadRow(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each DNS row must be an object");
        }

        return new MailDnsRow(
            ReadCheckKind(PaneValues.RequiredString(item, "kind")),
            ReadStatus(PaneValues.RequiredString(item, "status")),
            PaneValues.RequiredString(item, "detail"),
            MailPaneParsing.OptionalString(item, "fix"));
    }

    private static MailDnsCheckKind ReadCheckKind(string kind) => kind switch
    {
        "Spf" => MailDnsCheckKind.Spf,
        "Dkim" => MailDnsCheckKind.Dkim,
        "Dmarc" => MailDnsCheckKind.Dmarc,
        "Ptr" => MailDnsCheckKind.Ptr,
        "BulkSender" => MailDnsCheckKind.BulkSender,
        _ => throw new PaneModelException($"unknown DNS check '{kind}'"),
    };

    private static MailDnsStatus ReadStatus(string status) => status switch
    {
        "Pass" => MailDnsStatus.Pass,
        "Warn" => MailDnsStatus.Warn,
        "Fail" => MailDnsStatus.Fail,
        "NotApplicable" => MailDnsStatus.NotApplicable,
        _ => throw new PaneModelException($"unknown DNS status '{status}'"),
    };
}
