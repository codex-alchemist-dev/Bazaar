using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Bazaar.Server.MailDns;

/// <summary>
/// The mail DNS checker (doc 22, "DNS checker module (WP10)"). Runs the SPF, DKIM, DMARC, PTR and bulk-sender checks through an
/// <see cref="IDnsResolver"/> and returns advice, never a gate. A resolver failure becomes a result; nothing here throws on DNS.
/// Everything read from DNS is untrusted: it is capped, parsed and never echoed except in a reduced safe character set.
/// </summary>
public sealed partial class MailDnsChecker(IDnsResolver resolver) : IMailDnsChecker
{
    private const int _maxPtrAddresses = 4;
    private const int _maxPtrNames = 4;
    private const int _maxMxHosts = 4;

    [GeneratedRegex("^[a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._%+-]{1,64}@[A-Za-z0-9.-]{1,190}$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    public async Task<MailDnsReport> CheckAsync(MailDnsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var domain = Normalize(request.Domain);
        var bulk = CheckBulkSender(request.ExpectedDailyVolume);
        if (domain is null)
        {
            var invalid = new DnsFinding(DnsCheckStatus.Fail, "The mail domain is not a valid DNS name.", "Set a domain such as example.com.");
            return new MailDnsReport(
                string.Empty,
                [
                    ToResult(DnsCheckKind.Spf, [invalid]),
                    request.DkimSelector is null ? NotApplicable(DnsCheckKind.Dkim, "No DKIM selector was given.") : ToResult(DnsCheckKind.Dkim, [invalid]),
                    ToResult(DnsCheckKind.Dmarc, [invalid]),
                    request.SelfHosted ? ToResult(DnsCheckKind.Ptr, [invalid]) : NotApplicable(DnsCheckKind.Ptr, PtrNotApplicable),
                    bulk,
                ]);
        }

        var spf = Guarded(DnsCheckKind.Spf, () => CheckSpfAsync(domain, request, cancellationToken), cancellationToken);
        var dkim = Guarded(DnsCheckKind.Dkim, () => CheckDkimAsync(domain, request, cancellationToken), cancellationToken);
        var dmarc = Guarded(DnsCheckKind.Dmarc, () => CheckDmarcAsync(domain, request, cancellationToken), cancellationToken);
        var ptr = Guarded(DnsCheckKind.Ptr, () => CheckPtrAsync(domain, request, cancellationToken), cancellationToken);
        var results = await Task.WhenAll(spf, dkim, dmarc, ptr).ConfigureAwait(false);
        return new MailDnsReport(domain, [.. results, bulk]);
    }

    /// <summary>
    /// Lower-cases, strips a trailing dot, converts to ASCII (IDNA) and checks the shape. Returns null for anything that is not a
    /// DNS name. Public so the setup step and others use the same rule.
    /// </summary>
    public static string? Normalize(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var trimmed = domain.Trim().TrimEnd('.');
        try
        {
            trimmed = new IdnMapping().GetAscii(trimmed);
        }
        catch (ArgumentException)
        {
            return null;
        }

        trimmed = trimmed.ToLowerInvariant();
        return IsValidName(trimmed) ? trimmed : null;
    }

    /// <summary>True for a DNS name of at most 253 characters whose labels are letters, digits, hyphen and underscore.</summary>
    internal static bool IsValidName(string name) =>
        name.Length is > 0 and <= 253 && name.Split('.').All(label => LabelPattern().IsMatch(label.ToLowerInvariant()));

    private static async Task<DnsCheckResult> Guarded(DnsCheckKind kind, Func<Task<DnsCheckResult>> run, CancellationToken cancellationToken)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // A resolver is meant to return results. If a custom one throws, the check is reported as not completed, without the message.
            return new DnsCheckResult(kind, DnsCheckStatus.Warn, "The check could not be completed because the DNS resolver failed.", "Run the check again.");
        }
    }

    private static DnsCheckResult ToResult(DnsCheckKind kind, IReadOnlyList<DnsFinding> findings)
    {
        if (findings.Count == 0)
        {
            return new DnsCheckResult(kind, DnsCheckStatus.Pass, PassText(kind), string.Empty);
        }

        var worst = findings.Max(finding => finding.Status);
        var relevant = findings.Where(finding => finding.Status == worst).ToList();
        var others = findings.Where(finding => finding.Status != worst && finding.Status != DnsCheckStatus.Pass).ToList();
        var detail = string.Join(' ', relevant.Concat(others).Select(finding => finding.Detail));
        var fix = string.Join(' ', relevant.Concat(others).Select(finding => finding.Fix).Where(text => text.Length > 0));
        return new DnsCheckResult(kind, worst, detail, fix);
    }

    private static string PassText(DnsCheckKind kind) => kind switch
    {
        DnsCheckKind.Spf => "One SPF record, within the lookup limit, ending -all.",
        DnsCheckKind.Dkim => "The DKIM key record is published.",
        DnsCheckKind.Dmarc => "A DMARC policy is published.",
        DnsCheckKind.Ptr => "Reverse DNS is set and matches forward DNS.",
        _ => "Expected volume is under the bulk-sender threshold.",
    };

    private static DnsCheckResult NotApplicable(DnsCheckKind kind, string detail) =>
        new(kind, DnsCheckStatus.NotApplicable, detail, string.Empty);

    internal const string PtrNotApplicable = "The mail host is not self-hosted, so the provider owns reverse DNS.";

    private async Task<DnsCheckResult> CheckSpfAsync(string domain, MailDnsRequest request, CancellationToken cancellationToken)
    {
        var findings = await new SpfAnalyzer(resolver, domain, request.SenderSpfInclude).AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        return ToResult(DnsCheckKind.Spf, findings);
    }

    private async Task<DnsCheckResult> CheckDkimAsync(string domain, MailDnsRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DkimSelector))
        {
            return NotApplicable(DnsCheckKind.Dkim, "No DKIM selector was given, so DKIM was not checked.");
        }

        var selector = request.DkimSelector.Trim().ToLowerInvariant();
        if (!IsValidName(selector))
        {
            return ToResult(DnsCheckKind.Dkim, [new(DnsCheckStatus.Fail, "The DKIM selector is not a valid DNS label.", "Use the selector exactly as the sender gives it.")]);
        }

        var name = $"{selector}._domainkey.{domain}";
        var shown = MailDnsLimits.Safe(name);
        var txt = await resolver.TxtAsync(name, cancellationToken).ConfigureAwait(false);
        if (txt.Status == DnsLookupStatus.NxDomain)
        {
            return ToResult(DnsCheckKind.Dkim, [new(DnsCheckStatus.Fail, $"No DKIM record exists at {shown}.", "Publish the TXT record the sender provides at that name.")]);
        }

        if (txt.Status != DnsLookupStatus.Ok)
        {
            return ToResult(DnsCheckKind.Dkim, [SpfAnalyzer.LookupFailure(txt.Status, $"the DKIM record at {shown}", "Run the check again.")]);
        }

        var keys = txt.Records
            .Take(MailDnsLimits.MaxRecords)
            .Select(record => ParseTags(MailDnsLimits.CapTxt(record)))
            .Where(tags => tags.TryGetValue("v", out var version) && string.Equals(version, "DKIM1", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (keys.Count == 0)
        {
            return ToResult(DnsCheckKind.Dkim, [new(DnsCheckStatus.Fail, $"No DKIM key record (v=DKIM1) was found at {shown}.", "Publish the TXT record the sender provides at that name.")]);
        }

        if (keys.Any(tags => tags.TryGetValue("p", out var key) && key.Any(c => !char.IsWhiteSpace(c))))
        {
            return ToResult(DnsCheckKind.Dkim, []);
        }

        return ToResult(DnsCheckKind.Dkim, [new(DnsCheckStatus.Fail, $"The DKIM record at {shown} has an empty p= value, which means the key was revoked.", "Publish the current key the sender provides.")]);
    }

    private async Task<DnsCheckResult> CheckDmarcAsync(string domain, MailDnsRequest request, CancellationToken cancellationToken)
    {
        var name = "_dmarc." + domain;
        var expected = ValidAddress(request.DmarcReportAddress);
        var suggestion = $"Publish a TXT record at {name}: v=DMARC1; p=none; rua=mailto:{expected ?? "<report address>"}";

        var txt = await resolver.TxtAsync(name, cancellationToken).ConfigureAwait(false);
        if (txt.Status == DnsLookupStatus.NxDomain)
        {
            return ToResult(DnsCheckKind.Dmarc, [new(DnsCheckStatus.Fail, "No DMARC record exists.", suggestion)]);
        }

        if (txt.Status != DnsLookupStatus.Ok)
        {
            return ToResult(DnsCheckKind.Dmarc, [SpfAnalyzer.LookupFailure(txt.Status, "the DMARC record", "Run the check again.")]);
        }

        var policies = txt.Records
            .Take(MailDnsLimits.MaxRecords)
            .Select(record => ParseTags(MailDnsLimits.CapTxt(record)))
            .Where(tags => tags.TryGetValue("v", out var version) && string.Equals(version, "DMARC1", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (policies.Count == 0)
        {
            return ToResult(DnsCheckKind.Dmarc, [new(DnsCheckStatus.Fail, "No DMARC record (v=DMARC1) was found.", suggestion)]);
        }

        if (policies.Count > 1)
        {
            return ToResult(DnsCheckKind.Dmarc, [new(DnsCheckStatus.Fail, "There are several DMARC records. Receivers ignore all of them.", "Keep one v=DMARC1 TXT record.")]);
        }

        var tags = policies[0];
        var policy = tags.TryGetValue("p", out var p) ? p.ToLowerInvariant() : string.Empty;
        switch (policy)
        {
            case "quarantine" or "reject":
                return ToResult(DnsCheckKind.Dmarc, []);
            case "none":
                var reports = tags.TryGetValue("rua", out var rua) ? ReportAddresses(rua) : [];
                if (reports.Count == 0)
                {
                    return ToResult(DnsCheckKind.Dmarc, [new(DnsCheckStatus.Warn, "The DMARC policy is p=none with no rua=mailto: report address, so you receive no reports.", $"Add rua=mailto:{expected ?? "<report address>"} to the record.")]);
                }

                if (expected is not null && !reports.Contains(expected, StringComparer.OrdinalIgnoreCase))
                {
                    return ToResult(DnsCheckKind.Dmarc, [new(DnsCheckStatus.Warn, "The DMARC rua= address is not the configured report address.", $"Set rua=mailto:{expected}.")]);
                }

                return ToResult(DnsCheckKind.Dmarc, []);
            default:
                return ToResult(DnsCheckKind.Dmarc, [new(DnsCheckStatus.Fail, "The DMARC record has no valid p= policy.", "Set p=none to start, then quarantine and reject once reports are clean.")]);
        }
    }

    private async Task<DnsCheckResult> CheckPtrAsync(string domain, MailDnsRequest request, CancellationToken cancellationToken)
    {
        if (!request.SelfHosted)
        {
            return NotApplicable(DnsCheckKind.Ptr, PtrNotApplicable);
        }

        var addresses = request.SendingIp is { } given
            ? [given]
            : await SendingAddressesAsync(domain, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            return ToResult(DnsCheckKind.Ptr, [new(DnsCheckStatus.Warn, "The sending address could not be found from the domain's MX or address records.", "Set the sending IP in the plan, or publish an MX record for the domain.")]);
        }

        var findings = new List<DnsFinding>();
        foreach (var address in addresses.Take(_maxPtrAddresses))
        {
            var ptr = await resolver.PtrAsync(address, cancellationToken).ConfigureAwait(false);
            if (ptr.Status != DnsLookupStatus.Ok || ptr.Records.Count == 0)
            {
                findings.Add(ptr.Status is DnsLookupStatus.Timeout or DnsLookupStatus.ServFail
                    ? SpfAnalyzer.LookupFailure(ptr.Status, $"the reverse DNS of {address}", "Run the check again.")
                    : new(DnsCheckStatus.Fail, $"{address} has no reverse DNS (PTR) record.", "Ask the host provider to set a PTR record for the sending IP that names your mail host."));
                continue;
            }

            var confirmed = false;
            var unreadable = false;
            foreach (var host in ptr.Records.Take(_maxPtrNames).Select(name => name.Trim().TrimEnd('.')))
            {
                if (!IsValidName(host.ToLowerInvariant()))
                {
                    continue;
                }

                var forward = await resolver.AddressesAsync(host, cancellationToken).ConfigureAwait(false);
                if (forward.Status is DnsLookupStatus.Timeout or DnsLookupStatus.ServFail)
                {
                    unreadable = true;
                }
                else if (forward.Records.Any(candidate => candidate.Equals(address)))
                {
                    confirmed = true;
                    break;
                }
            }

            if (!confirmed)
            {
                findings.Add(unreadable
                    ? new(DnsCheckStatus.Warn, $"The forward lookup of the PTR name for {address} failed, so it could not be confirmed.", "Run the check again.")
                    : new(DnsCheckStatus.Fail, $"The PTR name of {address} does not resolve back to {address} (not forward-confirmed).", "Publish an A or AAAA record for the PTR name that points at the sending IP, or change the PTR."));
            }
        }

        return ToResult(DnsCheckKind.Ptr, findings);
    }

    /// <summary>The sending address when the plan gives none: the addresses of the domain's MX hosts, else the domain's own.</summary>
    private async Task<IReadOnlyList<IPAddress>> SendingAddressesAsync(string domain, CancellationToken cancellationToken)
    {
        var found = new List<IPAddress>();
        var mx = await resolver.MxAsync(domain, cancellationToken).ConfigureAwait(false);
        var hosts = mx.Status == DnsLookupStatus.Ok
            ? mx.Records.OrderBy(record => record.Preference).Take(_maxMxHosts).Select(record => record.Host.Trim().TrimEnd('.').ToLowerInvariant()).Where(IsValidName).ToList()
            : [];
        if (hosts.Count == 0)
        {
            hosts.Add(domain);
        }

        foreach (var host in hosts)
        {
            var lookup = await resolver.AddressesAsync(host, cancellationToken).ConfigureAwait(false);
            if (lookup.Status == DnsLookupStatus.Ok)
            {
                found.AddRange(lookup.Records.Take(MailDnsLimits.MaxRecords));
            }
        }

        return found.Distinct().Take(_maxPtrAddresses).ToList();
    }

    private static DnsCheckResult CheckBulkSender(int? expectedDailyVolume)
    {
        if (expectedDailyVolume is not { } volume)
        {
            return NotApplicable(DnsCheckKind.BulkSender, "No expected daily volume was given.");
        }

        if (volume > MailDnsLimits.BulkSenderDailyThreshold)
        {
            return new DnsCheckResult(
                DnsCheckKind.BulkSender,
                DnsCheckStatus.Warn,
                string.Create(CultureInfo.InvariantCulture, $"Over {MailDnsLimits.BulkSenderDailyThreshold:N0} messages a day counts as a bulk sender to Gmail, which requires SPF, DKIM and DMARC alignment and easy unsubscribe."),
                "Keep DMARC at quarantine or reject and watch the reports before raising volume.");
        }

        return ToResult(DnsCheckKind.BulkSender, []);
    }

    /// <summary>Parses <c>key=value; key=value</c> tags. Keys are lower-cased; the first occurrence of a key wins; at most 32 tags are read.</summary>
    private static Dictionary<string, string> ParseTags(string record)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in record.Split(';', 33, StringSplitOptions.RemoveEmptyEntries).Take(32))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            tags.TryAdd(part[..equals].Trim().ToLowerInvariant(), part[(equals + 1)..].Trim());
        }

        return tags;
    }

    /// <summary>The mailto addresses of a DMARC rua value, without any <c>!size</c> limit.</summary>
    private static List<string> ReportAddresses(string rua) =>
        [.. rua.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            .Select(item => item[7..].Split('!')[0].Trim())
            .Where(address => address.Length > 0)];

    private static string? ValidAddress(string? address) =>
        address is not null && AddressPattern().IsMatch(address.Trim()) ? address.Trim() : null;
}
