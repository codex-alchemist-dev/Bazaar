using Bazaar.Server.MailOutbound;

namespace Bazaar.Server.MailRuntime;

/// <summary>A fixed <see cref="IMailRuntimeOptionsSource"/>. The host may register a live one backed by its settings service.</summary>
public sealed class StaticMailRuntimeOptionsSource(MailRuntimeOptions options) : IMailRuntimeOptionsSource
{
    public MailRuntimeOptions Current { get; } = options;
}

/// <summary>
/// The shipped defaults for the outbound policy settings (<see cref="OutboundDefaults"/>). The settings service is in Bazaar.Server and not
/// reachable from a module, so a host that wants live values registers its own <see cref="IOutboundMailSettings"/> first.
/// </summary>
public sealed class DefaultOutboundMailSettings : IOutboundMailSettings
{
    /// <summary>Cloudflare's message ceiling (doc 22), used as the back end hard cap.</summary>
    public const long DefaultBackendHardCapBytes = 25L * 1024 * 1024;

    public int NewRecipientsPerAgentDay => OutboundDefaults.NewRecipientsPerAgentDay;

    public int SentPerDay => OutboundDefaults.SentPerDay;

    public int SentPerAgentDay => OutboundDefaults.SentPerAgentDay;

    public string? OperatorContact => null;

    public string? FooterWording => null;

    public long MaxMessageBytes => OutboundDefaults.MaxMessageBytes;

    public int MaxAttachments => OutboundDefaults.MaxAttachments;

    public long BackendHardCapBytes => DefaultBackendHardCapBytes;
}
