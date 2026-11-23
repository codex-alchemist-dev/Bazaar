namespace Bazaar.Server.MailDns;

/// <summary>What the SPF walk found, as findings the checker turns into one result.</summary>
internal sealed record DnsFinding(DnsCheckStatus Status, string Detail, string Fix);

/// <summary>
/// Reads one domain's SPF record and the records it pulls in. Counts DNS lookups (include, a, mx, ptr, exists, redirect) against
/// the limit of 10, with a depth cap, a loop guard and a cap on TXT fetches, so a hostile or looping record cannot make the
/// checker run away. The walk stops as soon as the lookup limit is passed.
/// </summary>
internal sealed class SpfAnalyzer(IDnsResolver resolver, string domain, string? senderInclude)
{
    private const int _maxFetches = 16;

    private readonly HashSet<string> _visited = new(StringComparer.OrdinalIgnoreCase) { domain };
    private readonly HashSet<string> _chainIncludes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DnsFinding> _findings = [];
    private int _lookups;
    private int _fetches;
    private bool _over;
    private char? _all;
    private bool _sawAll;

    /// <summary>True when <paramref name="text"/> is an SPF version record: <c>v=spf1</c>, then the end or a space.</summary>
    public static bool IsSpf(string text) =>
        text.StartsWith("v=spf1", StringComparison.OrdinalIgnoreCase) && (text.Length == 6 || text[6] == ' ');

    public async Task<IReadOnlyList<DnsFinding>> AnalyzeAsync(CancellationToken cancellationToken)
    {
        var txt = await resolver.TxtAsync(domain, cancellationToken).ConfigureAwait(false);
        _fetches++;
        if (txt.Status != DnsLookupStatus.Ok)
        {
            return [LookupFailure(txt.Status, "the SPF record", "Check that the domain's DNS is reachable, then run the check again.")];
        }

        var records = txt.Records.Take(MailDnsLimits.MaxRecords).Select(MailDnsLimits.CapTxt).Where(IsSpf).ToList();
        if (records.Count == 0)
        {
            return [new(DnsCheckStatus.Fail, "No SPF record (v=spf1) was found.", "Publish one TXT record at the domain: v=spf1 include:<your sender> -all")];
        }

        if (records.Count > 1)
        {
            return [new(DnsCheckStatus.Fail, "There are several SPF records. Mail servers treat that as an error.", "Merge them into a single v=spf1 TXT record and delete the others.")];
        }

        await WalkAsync(records[0], domain, 1, determinesAll: true, cancellationToken).ConfigureAwait(false);
        Conclude();
        return _findings;
    }

    private async Task WalkAsync(string record, string owner, int depth, bool determinesAll, CancellationToken cancellationToken)
    {
        string? redirect = null;
        var hasAll = false;
        foreach (var raw in record.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            if (_over)
            {
                return;
            }

            var term = raw;
            var qualifier = '+';
            if (term[0] is '+' or '-' or '~' or '?')
            {
                qualifier = term[0];
                term = term[1..];
            }

            var split = term.IndexOfAny([':', '/', '=']);
            var name = (split < 0 ? term : term[..split]).ToLowerInvariant();
            var value = split >= 0 && term[split] != '/' ? term[(split + 1)..] : string.Empty;

            switch (name)
            {
                case "all":
                    hasAll = true;
                    if (determinesAll)
                    {
                        _sawAll = true;
                        _all = qualifier;
                    }

                    break;
                case "include":
                    if (!CountLookup())
                    {
                        return;
                    }

                    if (determinesAll && value.Length > 0)
                    {
                        _chainIncludes.Add(value.TrimEnd('.'));
                    }

                    await FollowAsync(value, depth, determinesAll: false, cancellationToken).ConfigureAwait(false);
                    break;
                case "a" or "mx" or "ptr" or "exists":
                    CountLookup();
                    break;
                case "redirect" when split >= 0 && term[split] == '=':
                    redirect = value;
                    break;
            }
        }

        // A redirect applies only when the record has no "all" (RFC 7208 section 6.1), and it is a lookup of its own.
        if (!hasAll && redirect is { Length: > 0 } && !_over && CountLookup())
        {
            await FollowAsync(redirect, depth, determinesAll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Counts one lookup. Returns false once the limit is passed; the walk then stops.</summary>
    private bool CountLookup()
    {
        _lookups++;
        if (_lookups > MailDnsLimits.MaxSpfLookups)
        {
            _over = true;
        }

        return !_over;
    }

    private async Task FollowAsync(string target, int depth, bool determinesAll, CancellationToken cancellationToken)
    {
        var shown = MailDnsLimits.Safe(target);
        var key = target.TrimEnd('.');
        if (key.Length == 0 || !MailDnsChecker.IsValidName(key))
        {
            _findings.Add(new(DnsCheckStatus.Fail, $"The SPF record points at an invalid name ({shown}).", "Correct the include or redirect target."));
            return;
        }

        if (!_visited.Add(key))
        {
            _findings.Add(new(DnsCheckStatus.Fail, $"The SPF record loops back to {shown}.", "Remove the include or redirect that points back up the chain."));
            _over = true;
            return;
        }

        if (depth >= MailDnsLimits.MaxSpfDepth || _fetches >= _maxFetches)
        {
            _findings.Add(new(DnsCheckStatus.Fail, "The SPF include chain is too deep to evaluate.", "Flatten the chain; mail servers stop at 10 lookups."));
            _over = true;
            return;
        }

        _fetches++;
        var txt = await resolver.TxtAsync(key, cancellationToken).ConfigureAwait(false);
        if (txt.Status != DnsLookupStatus.Ok)
        {
            _findings.Add(LookupFailure(txt.Status, $"the SPF record of {shown}", "Check the include or redirect target, then run the check again."));
            return;
        }

        var records = txt.Records.Take(MailDnsLimits.MaxRecords).Select(MailDnsLimits.CapTxt).Where(IsSpf).ToList();
        if (records.Count != 1)
        {
            var detail = records.Count == 0
                ? $"{shown} has no SPF record, so the include fails."
                : $"{shown} has several SPF records, so the include fails.";
            _findings.Add(new(DnsCheckStatus.Fail, detail, "Correct the include target, or ask its owner to fix the record."));
            return;
        }

        await WalkAsync(records[0], key, depth + 1, determinesAll, cancellationToken).ConfigureAwait(false);
    }

    private void Conclude()
    {
        if (_over && _lookups > MailDnsLimits.MaxSpfLookups)
        {
            _findings.Add(new(
                DnsCheckStatus.Fail,
                $"The SPF record needs more than {MailDnsLimits.MaxSpfLookups} DNS lookups, so mail servers reject it.",
                "Remove includes you do not need, or replace them with ip4/ip6 ranges."));
        }

        if (_over)
        {
            return;
        }

        if (!_sawAll)
        {
            _findings.Add(new(DnsCheckStatus.Fail, "The SPF record has no 'all' ending, so any server may send as the domain.", "End the record with -all."));
        }
        else
        {
            switch (_all)
            {
                case '~':
                    _findings.Add(new(DnsCheckStatus.Warn, "The SPF record ends with ~all (soft fail).", "Change ~all to -all once the sender is confirmed."));
                    break;
                case '?':
                    _findings.Add(new(DnsCheckStatus.Fail, "The SPF record ends with ?all (neutral), which gives no protection.", "Change ?all to -all."));
                    break;
                case '+':
                    _findings.Add(new(DnsCheckStatus.Fail, "The SPF record ends with +all (or a bare all), which lets any server send as the domain.", "Change it to -all."));
                    break;
            }
        }

        if (!string.IsNullOrWhiteSpace(senderInclude))
        {
            var sender = senderInclude.Trim().TrimEnd('.');
            if (!_chainIncludes.Contains(sender))
            {
                _findings.Add(new(DnsCheckStatus.Warn, $"The SPF record does not include the sender ({MailDnsLimits.Safe(sender)}).", $"Add include:{MailDnsLimits.Safe(sender)} to the record."));
            }

            if (_chainIncludes.Any(include => !string.Equals(include, sender, StringComparison.OrdinalIgnoreCase)))
            {
                _findings.Add(new(DnsCheckStatus.Warn, "The SPF record also includes senders other than the configured one.", "Remove includes you do not use; the record should include the sender only."));
            }
        }
    }

    /// <summary>A failed lookup as a finding. A missing name is a fail; a timeout or server failure is advice only (DNS may be slow).</summary>
    internal static DnsFinding LookupFailure(DnsLookupStatus status, string what, string fix) => status switch
    {
        DnsLookupStatus.NxDomain => new(DnsCheckStatus.Fail, $"DNS has no entry for {what}.", fix),
        DnsLookupStatus.Timeout => new(DnsCheckStatus.Warn, $"Reading {what} timed out, so it could not be checked.", fix),
        _ => new(DnsCheckStatus.Warn, $"Reading {what} failed (server error), so it could not be checked.", fix),
    };
}
