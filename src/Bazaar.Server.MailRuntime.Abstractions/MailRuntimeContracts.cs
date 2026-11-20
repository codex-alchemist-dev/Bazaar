using Bazaar.Server.Gc.Write;

namespace Bazaar.Server.MailRuntime;

// Host-side wiring contracts for the mail pipelines (docs/design/22-email-subsystem.md, "Mail runtime (host wiring)").
// The runtime adds adapters and two schedulers; it adds no gate, counter, key service or GC writer.

/// <summary>
/// Operator settings for the two schedulers (PROPOSED keys <c>mail.runtime.enabled</c>, <c>mail.runtime.inbound_interval_seconds</c>,
/// <c>mail.runtime.outbound_interval_seconds</c>, <c>mail.runtime.outbound_resume_batch</c>). The effective values are never below the floors,
/// so a setting can slow a loop down and never speed it past what a mail provider tolerates.
/// </summary>
public sealed record MailRuntimeOptions
{
    /// <summary>Shipped inbound interval, in seconds.</summary>
    public const int DefaultInboundIntervalSeconds = 60;

    /// <summary>Floor for the inbound interval: IMAP providers throttle faster polling.</summary>
    public const int MinInboundIntervalSeconds = 15;

    /// <summary>Shipped outbound resume interval, in seconds.</summary>
    public const int DefaultOutboundIntervalSeconds = 30;

    /// <summary>Floor for the outbound resume interval.</summary>
    public const int MinOutboundIntervalSeconds = 10;

    /// <summary>Ceiling for either interval (one hour), so a typo cannot park a loop for days.</summary>
    public const int MaxIntervalSeconds = 3600;

    /// <summary>Shipped batch for one outbound resume pass.</summary>
    public const int DefaultOutboundResumeBatch = 50;

    /// <summary><c>mail.runtime.enabled</c>. False stops both loops from doing work (they keep waiting, so the setting can be turned back on).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary><c>mail.runtime.inbound_interval_seconds</c>.</summary>
    public int InboundIntervalSeconds { get; init; } = DefaultInboundIntervalSeconds;

    /// <summary><c>mail.runtime.outbound_interval_seconds</c>.</summary>
    public int OutboundIntervalSeconds { get; init; } = DefaultOutboundIntervalSeconds;

    /// <summary><c>mail.runtime.outbound_resume_batch</c>.</summary>
    public int OutboundResumeBatch { get; init; } = DefaultOutboundResumeBatch;

    /// <summary>Shipped lifetime of an outbound mail approval, in hours.</summary>
    public const int DefaultApprovalTtlHours = 24;

    /// <summary>Ceiling for the approval lifetime (30 days, the approvals queue's own ceiling).</summary>
    public const int MaxApprovalTtlHours = 720;

    /// <summary><c>mail.approval.ttl_hours</c> (PROPOSED). How long an owner approval for an outbound message stays open before it expires.</summary>
    public int ApprovalTtlHours { get; init; } = DefaultApprovalTtlHours;

    /// <summary>
    /// <c>mail.notices.gc_id</c> (PROPOSED): the GC that receives owner notices from the mail runtime. Null or empty skips the notice (counted, logged as a warning).
    /// </summary>
    public Guid? NoticesGcId { get; init; }

    /// <summary>The approval lifetime, kept between 1 hour and 30 days; zero or less means the default.</summary>
    public TimeSpan EffectiveApprovalTtl =>
        TimeSpan.FromHours(ApprovalTtlHours <= 0 ? DefaultApprovalTtlHours : Math.Min(ApprovalTtlHours, MaxApprovalTtlHours));

    /// <summary>The inbound interval after the floor and ceiling.</summary>
    public TimeSpan EffectiveInboundInterval =>
        TimeSpan.FromSeconds(Math.Clamp(InboundIntervalSeconds, MinInboundIntervalSeconds, MaxIntervalSeconds));

    /// <summary>The outbound interval after the floor and ceiling.</summary>
    public TimeSpan EffectiveOutboundInterval =>
        TimeSpan.FromSeconds(Math.Clamp(OutboundIntervalSeconds, MinOutboundIntervalSeconds, MaxIntervalSeconds));

    /// <summary>The resume batch, kept between 1 and 500.</summary>
    public int EffectiveOutboundResumeBatch => Math.Clamp(OutboundResumeBatch, 1, 500);
}

/// <summary>Where the loops read their options, once per iteration, so an operator change applies without a restart.</summary>
public interface IMailRuntimeOptionsSource
{
    MailRuntimeOptions Current { get; }
}

/// <summary>
/// Marks a default that exists only so the host can start: the backing module is not built or not wired. A loop whose required ports are
/// fail-closed does no work, so mail is never pulled (and marked seen) with nowhere safe to store it.
/// </summary>
public interface IFailClosedMailPort
{
    /// <summary>The port name, for the log and for the open item in doc 22.</summary>
    string PortName { get; }
}

/// <summary>Thrown by a fail-closed default that is called anyway. Carries the port name only.</summary>
public sealed class MailPortNotConfiguredException(string portName)
    : InvalidOperationException($"The mail port '{portName}' has no implementation wired in this host; the call was refused (fail closed).")
{
    public string PortName { get; } = portName;
}

/// <summary>
/// The one way the runtime writes to the GC log. Same shape as the GC log writer's single append, expressed with the
/// <c>Bazaar.GC.Abstractions</c> types so this module does not reference Bazaar.Server. The host forwards it to <c>IGcLogWriter.AppendAsync</c>.
/// </summary>
public interface IMailGcAppender
{
    Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct);
}

/// <summary>A forwarding <see cref="IMailGcAppender"/>: the host passes its writer's call as a delegate, so one line wires it.</summary>
public sealed class DelegateMailGcAppender(Func<AppendRequest, CancellationToken, Task<AppendResult>> append) : IMailGcAppender
{
    public Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct) => append(request, ct);
}

/// <summary>One row of <c>mail_domain</c> the runtime needs to build a back end. Holds a reference to configuration, never a credential.</summary>
/// <param name="Domain">The operator's domain, lower case.</param>
/// <param name="BackendKind"><c>imap_smtp</c>, <c>cloudflare</c>, <c>bazaar_client</c> or <c>builtin_server</c>.</param>
/// <param name="ConfigRef">The secret-store name of the domain's configuration, or null.</param>
public sealed record MailDomainConfig(string Domain, string BackendKind, string? ConfigRef);

/// <summary>Lists the domains whose back end should be live (state <c>active</c>).</summary>
public interface IMailDomainSource
{
    Task<IReadOnlyList<MailDomainConfig>> ListActiveAsync(CancellationToken ct);
}

/// <summary>The summary of one back end refresh: counts and closed error kinds, nothing from the configuration.</summary>
public sealed record MailBackendRefreshResult(int Built, int Skipped, IReadOnlyList<string> ErrorKinds);

/// <summary>
/// The resolver the outbound pipeline uses, plus the async refresh that builds its snapshot from the domain list and the secret store
/// (<c>Resolve</c> is synchronous, so it cannot read secrets itself).
/// </summary>
public interface IMailBackendCatalog : Bazaar.Server.MailOutbound.IMailBackendResolver
{
    Task<MailBackendRefreshResult> RefreshAsync(CancellationToken ct);
}
