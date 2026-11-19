using Bazaar.Server.Mail.Parsing;

namespace Bazaar.Server.Mail.Quarantine;

/// <summary>Gives the screening service an attachment's raw bytes by its position in <see cref="ParsedMail.Attachments"/>; null when they are not available.</summary>
public delegate ReadOnlyMemory<byte>? AttachmentBytesReader(int index);

/// <summary>The SPF, DKIM and DMARC results recorded for a message; null means not recorded.</summary>
public sealed record MailAuthResults(string? Spf, string? Dkim, string? Dmarc);

/// <summary>Settings for <see cref="MailScreeningService"/>: the shared limits and the extra protected domains (PROPOSED <c>mail.protected_domains</c>).</summary>
public sealed record MailScreeningOptions
{
    /// <summary>The one set of limits, used by both the archive inspection and the quarantine rules.</summary>
    public MailLimits Limits { get; init; } = MailLimits.Defaults;

    /// <summary>Extra domains to guard besides the operator's own mail domains. Empty by default.</summary>
    public IReadOnlyList<string> ExtraProtectedDomains { get; init; } = [];
}

/// <summary>
/// Runs the deterministic inbound screens for one parsed message (doc 22, inbound flow steps 2 to 4): reads each ZIP attachment's
/// directory with <see cref="ZipArchiveInspector"/> (never decompressing, and skipping any attachment larger than the message limit),
/// builds the options from <see cref="MailLimits"/> and <see cref="IProtectedMailDomains"/>, and returns the
/// <see cref="InboundQuarantineRules"/> decision. Every attachment whose bytes are a ZIP file is inspected, whatever its name,
/// so an archive renamed to <c>.txt</c> is still judged.
/// </summary>
public sealed class MailScreeningService(IProtectedMailDomains protectedDomains, MailScreeningOptions? options = null)
{
    private readonly MailScreeningOptions _options = options ?? new MailScreeningOptions();

    /// <summary>Screens one message.</summary>
    /// <param name="mail">The parsed message.</param>
    /// <param name="messageBytes">The raw message size in bytes.</param>
    /// <param name="auth">The authentication results.</param>
    /// <param name="attachmentBytes">Access to the raw attachment bytes.</param>
    /// <param name="links">Links found in the body, if the caller extracted them.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<QuarantineDecision> ScreenAsync(
        ParsedMail mail,
        long messageBytes,
        MailAuthResults auth,
        AttachmentBytesReader attachmentBytes,
        IReadOnlyList<LinkInfo>? links = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(attachmentBytes);
        var limits = _options.Limits;
        limits.Validate();

        var attachments = new List<AttachmentInfo>(mail.Attachments.Count);
        var largest = 0L;
        for (var i = 0; i < mail.Attachments.Count; i++)
        {
            var attachment = mail.Attachments[i];
            largest = Math.Max(largest, attachment.SizeBytes);
            ArchiveFacts? facts = null;
            if (attachment.SizeBytes <= limits.MaxMessageBytes && attachmentBytes(i) is { } bytes && bytes.Length <= limits.MaxMessageBytes)
            {
                facts = ZipArchiveInspector.Inspect(bytes.Span, limits);
            }

            attachments.Add(new AttachmentInfo(attachment.FileName ?? string.Empty, attachment.MediaType, facts));
        }

        var domains = await protectedDomains.GetAsync([.. _options.ExtraProtectedDomains], ct);
        var headers = new InboundHeaders(
            From: mail.FromAddress,
            SpfResult: auth.Spf,
            DkimResult: auth.Dkim,
            DmarcResult: auth.Dmarc,
            // An attachment bigger than the message is a lie about its size; the larger figure keeps the oversize screen honest.
            MessageBytes: Math.Max(messageBytes, largest),
            Attachments: attachments,
            Links: links ?? []);
        return InboundQuarantineRules.Evaluate(headers, new InboundQuarantineOptions { Limits = limits, ProtectedDomains = domains });
    }
}
