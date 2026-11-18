using System.Text.Json;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Mail.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Mail.Panes;

/// <summary>
/// The mail quarantine pane (pane kind <see cref="PaneKinds.MailQuarantine"/>, doc 22 WP12). Model:
/// <c>{ "digest": { "date", "held", "released", "purged", "reported", "topSenderDomains": [ { "domain", "count" } ] },
/// "items": [ { "senderDomain", "reasonCodes": [...], "authResult", "receivedAt", "state" } ] }</c>, where
/// <c>date</c> is the ISO day (yyyy-MM-dd) the digest covers and <c>state</c> is <c>held</c>, <c>released</c>, <c>purged</c>
/// or <c>reported</c>. Only those fields are read. A body
/// is never in this model, and any other field the server sends is ignored, so no message body reaches the screen.
/// A missing required field raises <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class MailQuarantinePaneViewModel : IPaneViewModel
{
    public MailQuarantinePaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Mail · Quarantine";

        var model = descriptor.Model;
        Digest = ReadDigest(MailPaneParsing.Object(model, "digest"));
        Rows = PaneValues.Array(model, "items").EnumerateArray().Select(ReadRow).ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>The daily digest: counts and top sender domains.</summary>
    public MailQuarantineDigest Digest { get; }

    /// <summary>The held messages, as metadata rows.</summary>
    public IReadOnlyList<MailQuarantineRow> Rows { get; }

    private static MailQuarantineDigest ReadDigest(JsonElement digest) => new(
        MailPaneParsing.IsoDate(digest, "date"),
        MailPaneParsing.Count(digest, "held"),
        MailPaneParsing.Count(digest, "released"),
        MailPaneParsing.Count(digest, "purged"),
        MailPaneParsing.Count(digest, "reported"),
        PaneValues.Array(digest, "topSenderDomains").EnumerateArray().Select(ReadDomainCount).ToArray());

    private static MailSenderDomainCount ReadDomainCount(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each top sender domain must be an object");
        }

        return new MailSenderDomainCount(
            PaneValues.RequiredString(item, "domain"),
            MailPaneParsing.Count(item, "count"));
    }

    private static MailQuarantineRow ReadRow(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new PaneModelException("each quarantine item must be an object");
        }

        return new MailQuarantineRow(
            PaneValues.RequiredString(item, "senderDomain"),
            MailPaneParsing.Strings(item, "reasonCodes"),
            PaneValues.RequiredString(item, "authResult"),
            MailPaneParsing.Date(item, "receivedAt"),
            ReadState(PaneValues.RequiredString(item, "state")));
    }

    private static MailQuarantineState ReadState(string state) => state switch
    {
        "held" => MailQuarantineState.Held,
        "released" => MailQuarantineState.Released,
        "purged" => MailQuarantineState.Purged,
        "reported" => MailQuarantineState.Reported,
        _ => throw new PaneModelException($"unknown quarantine state '{state}'"),
    };
}
