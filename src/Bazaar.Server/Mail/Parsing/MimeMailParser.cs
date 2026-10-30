using MimeKit;

namespace Bazaar.Server.Mail.Parsing;

/// <summary>
/// Parses raw RFC 5322 / MIME bytes with hard limits. Byte size is checked before parsing; depth, part and attachment
/// counts are checked on the parsed tree. No I/O; no content is trusted by this step.
/// </summary>
public sealed class MimeMailParser(MailLimits limits)
{
    private static readonly string[] _authHeaders = ["Authentication-Results", "Received-SPF", "DKIM-Signature", "ARC-Authentication-Results"];

    public MimeMailParser() : this(MailLimits.Defaults) { }

    public ParsedMail Parse(ReadOnlyMemory<byte> raw)
    {
        limits.Validate();
        if (raw.Length > limits.MaxMessageBytes)
            throw new MailLimitException("bytes", $"Message is {raw.Length} bytes; the limit is {limits.MaxMessageBytes}.");

        using var stream = new MemoryStream(raw.ToArray(), writable: false);
        var message = MimeMessage.Load(stream);

        var counter = new TreeCounter();
        if (message.Body is not null) Walk(message.Body, depth: 1, counter);
        if (counter.MaxDepth > limits.MaxDepth)
            throw new MailLimitException("depth", $"MIME depth {counter.MaxDepth} exceeds the limit of {limits.MaxDepth}.");
        if (counter.Parts > limits.MaxParts)
            throw new MailLimitException("parts", $"{counter.Parts} MIME parts exceed the limit of {limits.MaxParts}.");

        var attachments = new List<MailAttachment>();
        string? text = null;
        string? html = null;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part.IsAttachment)
            {
                attachments.Add(new MailAttachment(part.FileName, part.ContentType.MimeType, SizeOf(part)));
            }
            else if (IsType(part, "html"))
            {
                html ??= TextOf(part);
            }
            else if (IsType(part, "plain"))
            {
                text ??= TextOf(part);
            }
        }

        if (attachments.Count > limits.MaxAttachments)
            throw new MailLimitException("attachments", $"{attachments.Count} attachments exceed the limit of {limits.MaxAttachments}.");

        var auth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _authHeaders)
        {
            var value = message.Headers[name];
            if (value is not null) auth[name] = value;
        }

        return new ParsedMail(
            MessageId: message.MessageId,
            FromAddress: message.From.Mailboxes.FirstOrDefault()?.Address,
            Subject: message.Subject,
            TextBody: text,
            HtmlBody: html,
            Attachments: attachments,
            AutoSubmitted: IsAutoSubmitted(message),
            PartCount: counter.Parts,
            MaxDepthSeen: counter.MaxDepth,
            AuthenticationResults: auth);
    }

    private static bool IsAutoSubmitted(MimeMessage message)
    {
        // RFC 3834: any value other than "no" marks an automatic reply or notice. Such mail can loop, so it is flagged.
        var value = message.Headers["Auto-Submitted"];
        return value is not null && !value.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsType(MimePart part, string subtype) =>
        part.ContentType.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase)
        && part.ContentType.MediaSubtype.Equals(subtype, StringComparison.OrdinalIgnoreCase);

    private static string TextOf(MimePart part)
    {
        using var buffer = new MemoryStream();
        part.Content?.DecodeTo(buffer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static long SizeOf(MimePart part)
    {
        using var buffer = new MemoryStream();
        part.Content?.DecodeTo(buffer);
        return buffer.Length;
    }

    private static void Walk(MimeEntity? entity, int depth, TreeCounter counter)
    {
        if (entity is null) return;
        counter.Parts++;
        counter.MaxDepth = Math.Max(counter.MaxDepth, depth);
        if (entity is Multipart multipart)
        {
            foreach (var child in multipart) Walk(child, depth + 1, counter);
        }
        else if (entity is MessagePart { Message: { } inner })
        {
            Walk(inner.Body, depth + 1, counter);
        }
    }

    private sealed class TreeCounter
    {
        public int Parts;
        public int MaxDepth;
    }
}
