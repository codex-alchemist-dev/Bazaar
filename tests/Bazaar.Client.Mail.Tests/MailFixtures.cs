using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Tests;

/// <summary>
/// Fixture models in the shape the server sends (doc 22, WP12). They use reserved example domains and addresses only. The
/// quarantine fixture carries a body on purpose, to prove the screen never shows it.
/// </summary>
internal static class MailFixtures
{
    public const string QuarantineModel = """
        {"digest":{"held":2,"released":1,"purged":0,"reported":1,
          "topSenderDomains":[{"domain":"mail.example.net","count":2}]},
         "items":[
           {"senderDomain":"mail.example.net","reasonCodes":["spf_fail","new_sender"],"authResult":"spf=fail dkim=none",
            "receivedAt":"2026-11-17T09:30:00+00:00","state":"held",
            "subject":"Urgent","body":"SECRET-BODY-TEXT wire the money today"},
           {"senderDomain":"partner.example.org","reasonCodes":[],"authResult":"dkim=pass",
            "receivedAt":"2026-11-16T22:05:00-05:00","state":"released"}
         ]}
        """;

    public const string MailboxModel = """
        {"messages":[
          {"from":"Sam <sam@partner.example.org>","subject":"Invoice <b>now</b>",
           "receivedAt":"2026-11-17T10:00:00+00:00",
           "text":"Hello <img src=\"https://tracker.example/p.gif\"> world",
           "links":["https://docs.partner.example.org/invoice?id=7","http://user@evil.example:8080/login","javascript:alert(1)"],
           "html":"<p>HTML version</p>"}
        ]}
        """;

    public const string ApprovalModel = """
        {"from":"agent@bazaar.example","recipients":["buyer@partner.example.org","cc@partner.example.org"],
         "subject":"Renewal terms","body":"Please see the attached <b>terms</b>.",
         "footerPreview":"Sent by an AI agent for the operator. Contact: ops@example.org",
         "reason":"The body is built from inbound mail, so the send needs the owner."}
        """;

    public const string DnsModel = """
        {"domain":"example.com","rows":[
          {"kind":"Spf","status":"Warn","detail":"SPF ends in ~all.","fix":"End the record with -all."},
          {"kind":"Dkim","status":"Pass","detail":"The DKIM key is present.","fix":null},
          {"kind":"Dmarc","status":"Fail","detail":"No DMARC record was found.","fix":"Publish a DMARC record with p=none."},
          {"kind":"Ptr","status":"NotApplicable","detail":"The back end is hosted.","fix":null},
          {"kind":"BulkSender","status":"Warn","detail":"Expected volume is over 5,000 a day.","fix":"Plan for bulk-sender rules."}
        ]}
        """;

    public static PaneDescriptor Descriptor(string kind, string json) =>
        new(kind, "Title", string.Empty, JsonElement.Parse(json));

    public static PaneRegistry Registry() => PaneRegistry.Compose(MailPanes.Modules);

    /// <summary>The text of every text block under the view, in tree order.</summary>
    public static IReadOnlyList<string> Texts(Control view) =>
        view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToArray();

    /// <summary>True when some control under the view carries this automation name.</summary>
    public static bool HasAutomationName(Control view, string name) =>
        view.GetLogicalDescendants().OfType<Control>().Any(c => AutomationProperties.GetName(c) == name);
}
