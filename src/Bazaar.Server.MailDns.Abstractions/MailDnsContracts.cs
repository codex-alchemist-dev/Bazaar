using System.Net;

namespace Bazaar.Server.MailDns;

// DNS checker contracts (docs/design/22-email-subsystem.md, "DNS checker module (WP10)"). Protocol-free: the shipped resolver
// sits behind IDnsResolver, so tests run on a stub and CI needs no network. Nothing here names a domain, provider or selector.

/// <summary>How one DNS lookup ended. A failure is a result, never an exception the checker lets escape.</summary>
public enum DnsLookupStatus
{
    /// <summary>The query was answered. The record list may be empty (the name exists but has no record of that type).</summary>
    Ok,

    /// <summary>The name does not exist.</summary>
    NxDomain,

    /// <summary>The server failed, refused, or the network path was unreachable.</summary>
    ServFail,

    /// <summary>No answer arrived within the per-query timeout.</summary>
    Timeout,
}

/// <summary>The outcome of one lookup: a status and the records, already capped by the resolver. Records are untrusted text.</summary>
public sealed record DnsLookup<T>(DnsLookupStatus Status, IReadOnlyList<T> Records);

/// <summary>Builders for <see cref="DnsLookup{T}"/>.</summary>
public static class DnsLookup
{
    /// <summary>An answered query holding <paramref name="records"/>.</summary>
    public static DnsLookup<T> Ok<T>(IReadOnlyList<T> records) => new(DnsLookupStatus.Ok, records);

    /// <summary>A failed query with no records.</summary>
    public static DnsLookup<T> Failed<T>(DnsLookupStatus status) => new(status, []);
}

/// <summary>One MX record.</summary>
public sealed record DnsMxRecord(int Preference, string Host);

/// <summary>
/// Reads DNS. Every method returns a result, including on timeout, NXDOMAIN and SERVFAIL. A TXT record is returned as one string
/// (its character-strings joined). Only caller cancellation throws.
/// </summary>
public interface IDnsResolver
{
    Task<DnsLookup<string>> TxtAsync(string name, CancellationToken cancellationToken);

    Task<DnsLookup<string>> PtrAsync(IPAddress address, CancellationToken cancellationToken);

    /// <summary>A and AAAA records of <paramref name="name"/>.</summary>
    Task<DnsLookup<IPAddress>> AddressesAsync(string name, CancellationToken cancellationToken);

    Task<DnsLookup<DnsMxRecord>> MxAsync(string name, CancellationToken cancellationToken);
}

/// <summary>The checks the report holds, one result each.</summary>
public enum DnsCheckKind
{
    Spf,
    Dkim,
    Dmarc,

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "PTR is the DNS record type name (doc 22).")]
    Ptr,
    BulkSender,
}

/// <summary>The verdict of one check. The report is advice: a <see cref="Fail"/> never blocks setup.</summary>
public enum DnsCheckStatus
{
    Pass,
    Warn,
    Fail,
    NotApplicable,
}

/// <summary>Back end kind names accepted in <see cref="MailDnsRequest.BackendKind"/> (doc 45 step 11).</summary>
public static class MailDnsBackendKinds
{
    public const string Generic = "generic";

    public const string Cloudflare = "cloudflare";

    public const string BuiltIn = "builtin";

    /// <summary>True when <paramref name="kind"/> is a known back end kind, compared without case.</summary>
    public static bool IsKnown(string? kind) =>
        string.Equals(kind, Generic, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, Cloudflare, StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, BuiltIn, StringComparison.OrdinalIgnoreCase);
}

/// <summary>What to check. Everything except <paramref name="Domain"/> is optional.</summary>
/// <param name="Domain">The operator's mail domain.</param>
/// <param name="BackendKind">One of <see cref="MailDnsBackendKinds"/>, or null when unknown.</param>
/// <param name="DkimSelector">The selector the sender provides. Null means DKIM is not checked.</param>
/// <param name="SenderSpfInclude">The sender's SPF include domain, when the back end gives one.</param>
/// <param name="DmarcReportAddress">The operator's report address the DMARC <c>rua</c> should name.</param>
/// <param name="SelfHosted">True when the operator runs the sending host, so PTR applies.</param>
/// <param name="ExpectedDailyVolume">Expected messages a day, for the bulk-sender warning.</param>
/// <param name="SendingIp">The sending address, for the PTR check. Null: derived from the domain's MX hosts.</param>
public sealed record MailDnsRequest(
    string Domain,
    string? BackendKind = null,
    string? DkimSelector = null,
    string? SenderSpfInclude = null,
    string? DmarcReportAddress = null,
    bool SelfHosted = false,
    int? ExpectedDailyVolume = null,
    IPAddress? SendingIp = null);

/// <summary>One check's result: plain words for the operator, and a fix hint (empty on a pass).</summary>
public sealed record DnsCheckResult(DnsCheckKind Kind, DnsCheckStatus Status, string Detail, string Fix);

/// <summary>All checks for one domain.</summary>
public sealed record MailDnsReport(string Domain, IReadOnlyList<DnsCheckResult> Results)
{
    /// <summary>How many results have <paramref name="status"/>.</summary>
    public int Count(DnsCheckStatus status) => Results.Count(result => result.Status == status);

    /// <summary>The result of <paramref name="kind"/>.</summary>
    public DnsCheckResult this[DnsCheckKind kind] => Results.First(result => result.Kind == kind);
}

/// <summary>Runs the DNS checks. Never throws on a DNS failure; only caller cancellation escapes.</summary>
public interface IMailDnsChecker
{
    Task<MailDnsReport> CheckAsync(MailDnsRequest request, CancellationToken cancellationToken);
}
