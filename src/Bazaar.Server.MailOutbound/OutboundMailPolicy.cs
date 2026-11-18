using System.Security.Cryptography;
using System.Text;
using Bazaar.RateLimits;

namespace Bazaar.Server.MailOutbound;

/// <summary>
/// The outbound send policy (doc 22, outbound flow). Deterministic, no network: it reads ports and settings and never sends.
/// Order: shape, Bazaar-to-Bazaar, size limits, then the rate limiter (Pause), then the tier. Settings can tighten a limit and
/// can never loosen a floor: hard taint is always Tier 2 and the footer is never empty. Counters are the shared
/// <see cref="IRateLimiter"/>: a send that reaches the limiter step consumes one send slot and one slot per new recipient.
/// </summary>
public sealed class OutboundMailPolicy(
    IBazaarAddressDirectory directory,
    IApprovedRecipients approved,
    IRateLimiter limiter,
    IOutboundMailSettings settings) : IOutboundMailPolicy
{
    /// <summary>Doc 22 <c>mail.new_recipients_per_agent_day</c> as a shared-contract policy. The limit is replaced by the setting.</summary>
    public static readonly RateLimitPolicy NewRecipientsPerAgentDay = new(
        "mail_new_recipients_per_agent_day", OutboundDefaults.NewRecipientsPerAgentDay, TimeSpan.FromDays(1), RateLimitScope.Agent,
        "mail.new_recipients_per_agent_day");

    private static readonly System.Buffers.SearchValues<char> _forbidden = System.Buffers.SearchValues.Create(" \t\r\n<>,;\"");

    public async Task<OutboundDecision> EvaluateAsync(OutboundRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var recipients = request.Recipients.Select(r => r.Trim()).Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (recipients.Length == 0) return Refuse(OutboundReason.NoRecipients);
        if (recipients.Any(r => !IsPlainAddress(r)) || request.Subject.IndexOfAny(['\r', '\n']) >= 0) return Refuse(OutboundReason.InvalidMessage);

        // Internal traffic stays in GCs. Only a message whose every recipient is a Bazaar address is refused (mixed: PROPOSED gap).
        var allBazaar = true;
        foreach (var recipient in recipients)
        {
            if (!await directory.IsBazaarAddressAsync(recipient, ct).ConfigureAwait(false))
            {
                allBazaar = false;
                break;
            }
        }

        if (allBazaar) return Refuse(OutboundReason.BazaarToBazaar);

        if (request.AttachmentCount > EffectiveMaxAttachments(settings)) return Refuse(OutboundReason.TooManyAttachments);
        var size = Encoding.UTF8.GetByteCount(request.Subject) + Encoding.UTF8.GetByteCount(request.TextBody) + Math.Max(0, request.AttachmentBytes);
        if (size > EffectiveMaxBytes(settings)) return Refuse(OutboundReason.TooLarge);

        var newRecipients = 0;
        foreach (var recipient in recipients)
        {
            if (!await approved.IsApprovedAsync(request.AgentId, recipient, ct).ConfigureAwait(false)) newRecipients++;
        }

        // Rate limits through the shared contract. A denied call is not counted, so a retry does not extend the penalty.
        // A limit of 0 is the tightest setting (the limiter itself needs at least 1), so it pauses without asking the limiter.
        var sendPolicy = SendPolicy(settings);
        if (sendPolicy.Limit == 0) return Pause(OutboundReason.DailyQuota, sendPolicy.Window);
        var quota = limiter.TryAcquire(sendPolicy, request.AgentId);
        if (!quota.Allowed) return Pause(OutboundReason.DailyQuota, quota.RetryAfter);

        var capPolicy = NewRecipientPolicy(settings);
        if (newRecipients > 0 && capPolicy.Limit == 0) return Pause(OutboundReason.NewRecipientCap, capPolicy.Window);
        for (var i = 0; i < newRecipients; i++)
        {
            var slot = limiter.TryAcquire(capPolicy, request.AgentId);
            if (!slot.Allowed) return Pause(OutboundReason.NewRecipientCap, slot.RetryAfter);
        }

        var footer = EffectiveFooter(settings);
        var key = OutboundIdempotency.Derive(request);

        // Tier 2 reasons in a fixed order, so the code is stable. Hard taint comes first and no setting reaches it.
        OutboundReason? tier2 =
            request.Taint == MailTaint.Hard ? OutboundReason.HardTaint
            : newRecipients > 0 ? OutboundReason.NewRecipient
            : request.DerivedFromInboundMail ? OutboundReason.DerivedFromInboundMail
            : request.AttachmentCount > 0 ? OutboundReason.Attachment
            : request.IsSignUpReply ? OutboundReason.SignUpReply
            : !request.UsesTemplate ? OutboundReason.NotTemplate
            : null;

        return tier2 is { } reason
            ? new OutboundDecision(OutboundDecisionKind.NeedsOwnerApproval, reason, 2, key, footer)
            : new OutboundDecision(OutboundDecisionKind.Send, OutboundReason.Tier1Template, 1, key, footer);
    }

    /// <summary>The footer to append: the setting, or the shipped footer when it is empty or whitespace. Never empty.</summary>
    public static string EffectiveFooter(IOutboundMailSettings settings) =>
        string.IsNullOrWhiteSpace(settings.FooterWording) ? OutboundDefaults.Footer : settings.FooterWording.Trim();

    /// <summary>The byte limit: the setting (default when not positive), capped by the back end's hard cap.</summary>
    public static long EffectiveMaxBytes(IOutboundMailSettings settings)
    {
        var configured = settings.MaxMessageBytes > 0 ? settings.MaxMessageBytes : OutboundDefaults.MaxMessageBytes;
        return settings.BackendHardCapBytes > 0 ? Math.Min(configured, settings.BackendHardCapBytes) : configured;
    }

    /// <summary>The attachment limit: the setting, or the default when negative.</summary>
    public static int EffectiveMaxAttachments(IOutboundMailSettings settings) =>
        settings.MaxAttachments < 0 ? OutboundDefaults.MaxAttachments : settings.MaxAttachments;

    /// <summary>The shared <see cref="SharedRateLimitPolicies.MailSentPerDay"/> row with the setting's limit (default when negative).</summary>
    public static RateLimitPolicy SendPolicy(IOutboundMailSettings settings) =>
        SharedRateLimitPolicies.MailSentPerDay with { Limit = settings.SentPerDay < 0 ? OutboundDefaults.SentPerDay : settings.SentPerDay };

    /// <summary>The new-recipient cap with the setting's limit (default when negative).</summary>
    public static RateLimitPolicy NewRecipientPolicy(IOutboundMailSettings settings) =>
        NewRecipientsPerAgentDay with { Limit = settings.NewRecipientsPerAgentDay < 0 ? OutboundDefaults.NewRecipientsPerAgentDay : settings.NewRecipientsPerAgentDay };

    private static bool IsPlainAddress(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at == address.LastIndexOf('@') && at < address.Length - 1
            && address.AsSpan().IndexOfAny(_forbidden) < 0;
    }

    private static OutboundDecision Refuse(OutboundReason reason) => new(OutboundDecisionKind.Refuse, reason, 0, string.Empty, string.Empty);

    private static OutboundDecision Pause(OutboundReason reason, TimeSpan retryAfter) =>
        new(OutboundDecisionKind.Pause, reason, 0, string.Empty, string.Empty, retryAfter);
}

/// <summary>Derives the idempotency key (doc 22, outbound step 4). The same content and nonce always give the same key.</summary>
public static class OutboundIdempotency
{
    /// <summary>
    /// A stable SHA-256 over agent, mailbox, recipients (sorted, lower case, de-duplicated), subject, body and the caller nonce.
    /// Fields are length-prefixed so no two different requests share an input. Taint and flags are not part of the key.
    /// </summary>
    public static string Derive(OutboundRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var recipients = request.Recipients.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).Distinct().Order(StringComparer.Ordinal);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, request.AgentId);
        Add(hash, request.From.Address.ToLowerInvariant());
        foreach (var recipient in recipients) Add(hash, recipient);
        Add(hash, "\u0001");
        Add(hash, request.Subject);
        Add(hash, request.TextBody);
        Add(hash, request.Nonce);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Add(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
