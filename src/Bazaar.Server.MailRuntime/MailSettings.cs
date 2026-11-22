using Bazaar.Server.MailOutbound;
using Bazaar.Server.Settings;

namespace Bazaar.Server.MailRuntime;

/// <summary>
/// The mail settings (docs/design/22-email-subsystem.md, "Mail settings registration"): the runtime loop keys and the outbound policy keys. All
/// install scope and operator-changed; changes go through the settings service, which audits them. Each default is the constant the code already
/// ships, so registering the keys changes no behaviour. A floor or ceiling lives in the validator: a write outside it is rejected and a stored value
/// outside it is never read, so a setting tightens and never loosens past the floor. <c>security.quota.mail_sent_per_day</c> belongs to doc 15 and is
/// not redefined here.
/// </summary>
public static class MailSettings
{
    /// <summary>Highest value for the daily send limits; a typo cannot lift the limit to billions.</summary>
    public const int MaxDailyLimit = 10_000;

    /// <summary>Most attachments a message may carry.</summary>
    public const int MaxAttachmentsCeiling = 50;

    public const int MaxFooterWordingLength = 1000;

    public const int MaxOperatorContactLength = 200;

    public static readonly SettingKey<bool> RuntimeEnabled = new(
        "mail.runtime.enabled", true, SettingScope.Install, SettingRole.Operator,
        "Run the inbound and outbound mail loops. Off stops both from doing work; they keep waiting, so turning it back on needs no restart.");

    public static readonly SettingKey<int> InboundIntervalSeconds = new(
        "mail.runtime.inbound_interval_seconds", MailRuntimeOptions.DefaultInboundIntervalSeconds, SettingScope.Install, SettingRole.Operator,
        "Seconds between inbound mail passes. Not below the floor (mail providers throttle faster polling).",
        validate: Between(MailRuntimeOptions.MinInboundIntervalSeconds, MailRuntimeOptions.MaxIntervalSeconds, "mail.runtime.inbound_interval_seconds"));

    public static readonly SettingKey<int> OutboundIntervalSeconds = new(
        "mail.runtime.outbound_interval_seconds", MailRuntimeOptions.DefaultOutboundIntervalSeconds, SettingScope.Install, SettingRole.Operator,
        "Seconds between outbound resume passes. Not below the floor.",
        validate: Between(MailRuntimeOptions.MinOutboundIntervalSeconds, MailRuntimeOptions.MaxIntervalSeconds, "mail.runtime.outbound_interval_seconds"));

    public static readonly SettingKey<int> OutboundResumeBatch = new(
        "mail.runtime.outbound_resume_batch", MailRuntimeOptions.DefaultOutboundResumeBatch, SettingScope.Install, SettingRole.Operator,
        "Most messages one outbound resume pass picks up.",
        validate: Between(1, 500, "mail.runtime.outbound_resume_batch"));

    public static readonly SettingKey<long> MaxMessageBytes = new(
        "mail.max_message_bytes", OutboundDefaults.MaxMessageBytes, SettingScope.Install, SettingRole.Operator,
        "Largest outbound message in bytes. Cannot exceed the back end's hard cap (25 MiB on Cloudflare).",
        validate: value => value >= 1 && value <= DefaultOutboundMailSettings.DefaultBackendHardCapBytes
            ? null
            : $"'mail.max_message_bytes' must be from 1 to {DefaultOutboundMailSettings.DefaultBackendHardCapBytes}; got {value}.");

    public static readonly SettingKey<int> MaxAttachments = new(
        "mail.max_attachments", OutboundDefaults.MaxAttachments, SettingScope.Install, SettingRole.Operator,
        "Most attachments on one outbound message.",
        validate: Between(0, MaxAttachmentsCeiling, "mail.max_attachments"));

    public static readonly SettingKey<int> NewRecipientsPerAgentDay = new(
        "mail.new_recipients_per_agent_day", OutboundDefaults.NewRecipientsPerAgentDay, SettingScope.Install, SettingRole.Operator,
        "New recipients one agent may write to per day. Raising it above the shipped value needs owner approval; a breach pauses the mailbox.",
        approvalTier: ApprovalTier.Tier2,
        validate: Between(0, MaxDailyLimit, "mail.new_recipients_per_agent_day"),
        requiresApprovalFor: value => value > OutboundDefaults.NewRecipientsPerAgentDay);

    public static readonly SettingKey<int> SentPerAgentDay = new(
        "mail.sent_per_agent_day", OutboundDefaults.SentPerAgentDay, SettingScope.Install, SettingRole.Operator,
        "Messages one agent may send per day, on top of the install-wide quota. Raising it above the shipped value needs owner approval.",
        approvalTier: ApprovalTier.Tier2,
        validate: Between(0, MaxDailyLimit, "mail.sent_per_agent_day"),
        requiresApprovalFor: value => value > OutboundDefaults.SentPerAgentDay);

    public static readonly SettingKey<string> FooterWording = new(
        "mail.footer.wording", string.Empty, SettingScope.Install, SettingRole.Operator,
        "Wording of the footer on outbound mail. Empty uses the shipped footer, which states that an AI agent sent the mail. The footer itself cannot be turned off, and a change needs approval (counsel).",
        approvalTier: ApprovalTier.Tier2,
        validate: value => value.Length <= MaxFooterWordingLength ? null : $"'mail.footer.wording' must be at most {MaxFooterWordingLength} characters.",
        requiresApprovalFor: value => !string.IsNullOrWhiteSpace(value));

    public static readonly SettingKey<string> FooterOperatorContact = new(
        "mail.footer.operator_contact", string.Empty, SettingScope.Install, SettingRole.Operator,
        "Operator contact line added under the footer. Empty adds none. It never replaces the footer.",
        validate: value => value.Length <= MaxOperatorContactLength ? null : $"'mail.footer.operator_contact' must be at most {MaxOperatorContactLength} characters.");

    /// <summary>Every mail key.</summary>
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        RuntimeEnabled,
        InboundIntervalSeconds,
        OutboundIntervalSeconds,
        OutboundResumeBatch,
        MaxMessageBytes,
        MaxAttachments,
        NewRecipientsPerAgentDay,
        SentPerAgentDay,
        FooterWording,
        FooterOperatorContact,
    ];

    private static Func<int, string?> Between(int min, int max, string name) =>
        value => value >= min && value <= max ? null : $"'{name}' must be from {min} to {max}; got {value}.";
}

/// <summary>The mail module's contribution to the settings registry. <c>AddBazaarMailRuntime</c> registers it; the host composes it with the built-in keys.</summary>
public sealed class MailSettingContribution : ISettingContribution
{
    public IReadOnlyList<SettingDefinition> Settings => MailSettings.All;
}

/// <summary>
/// Reads one setting value. The settings service lives in Bazaar.Server, which no module may reference, so the host supplies this port over
/// <c>ISettingStore</c>. An unset key returns its default; an invalid stored value throws.
/// </summary>
public interface IMailSettingReader
{
    Task<T> GetAsync<T>(SettingKey<T> key, CancellationToken ct);
}

/// <summary>An options source that reloads from the settings service. The loops call <see cref="RefreshAsync"/> before each pass and each wait.</summary>
public interface IRefreshableMailRuntimeOptionsSource : IMailRuntimeOptionsSource
{
    Task RefreshAsync(CancellationToken ct);
}

/// <summary>
/// The runtime options and the outbound policy settings, read through <see cref="IMailSettingReader"/> into a snapshot. Both consumers are
/// synchronous, so reads come from the last good snapshot. Until the first refresh succeeds, and whenever a refresh fails, the values are the
/// ones the module was configured with (the fallback), so a settings outage never stops mail and never loosens a limit.
/// </summary>
public sealed class SettingBackedMailSettings(IMailSettingReader reader, MailRuntimeOptions? fallback = null)
    : IRefreshableMailRuntimeOptionsSource, IOutboundMailSettings
{
    private sealed record Snapshot(
        MailRuntimeOptions Runtime,
        int NewRecipientsPerAgentDay,
        int SentPerAgentDay,
        string? OperatorContact,
        string? FooterWording,
        long MaxMessageBytes,
        int MaxAttachments);

    private Snapshot _snapshot = new(
        fallback ?? new MailRuntimeOptions(),
        OutboundDefaults.NewRecipientsPerAgentDay,
        OutboundDefaults.SentPerAgentDay,
        null,
        null,
        OutboundDefaults.MaxMessageBytes,
        OutboundDefaults.MaxAttachments);

    public MailRuntimeOptions Current => Volatile.Read(ref _snapshot).Runtime;

    public int NewRecipientsPerAgentDay => Volatile.Read(ref _snapshot).NewRecipientsPerAgentDay;

    /// <summary>Owned by doc 15 (<c>security.quota.mail_sent_per_day</c>); the shipped default until that key has its own reader.</summary>
    public int SentPerDay => OutboundDefaults.SentPerDay;

    public int SentPerAgentDay => Volatile.Read(ref _snapshot).SentPerAgentDay;

    public string? OperatorContact => Volatile.Read(ref _snapshot).OperatorContact;

    public string? FooterWording => Volatile.Read(ref _snapshot).FooterWording;

    public long MaxMessageBytes => Volatile.Read(ref _snapshot).MaxMessageBytes;

    public int MaxAttachments => Volatile.Read(ref _snapshot).MaxAttachments;

    public long BackendHardCapBytes => DefaultOutboundMailSettings.DefaultBackendHardCapBytes;

    /// <summary>
    /// Reads every key and swaps the snapshot in one step. If any read throws, the previous snapshot stays (all or nothing), and the exception
    /// goes to the caller, which logs its type only.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var runtime = new MailRuntimeOptions
        {
            Enabled = await reader.GetAsync(MailSettings.RuntimeEnabled, ct).ConfigureAwait(false),
            InboundIntervalSeconds = await reader.GetAsync(MailSettings.InboundIntervalSeconds, ct).ConfigureAwait(false),
            OutboundIntervalSeconds = await reader.GetAsync(MailSettings.OutboundIntervalSeconds, ct).ConfigureAwait(false),
            OutboundResumeBatch = await reader.GetAsync(MailSettings.OutboundResumeBatch, ct).ConfigureAwait(false),
        };
        var next = new Snapshot(
            runtime,
            await reader.GetAsync(MailSettings.NewRecipientsPerAgentDay, ct).ConfigureAwait(false),
            await reader.GetAsync(MailSettings.SentPerAgentDay, ct).ConfigureAwait(false),
            NullIfBlank(await reader.GetAsync(MailSettings.FooterOperatorContact, ct).ConfigureAwait(false)),
            NullIfBlank(await reader.GetAsync(MailSettings.FooterWording, ct).ConfigureAwait(false)),
            await reader.GetAsync(MailSettings.MaxMessageBytes, ct).ConfigureAwait(false),
            await reader.GetAsync(MailSettings.MaxAttachments, ct).ConfigureAwait(false));
        Volatile.Write(ref _snapshot, next);
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
