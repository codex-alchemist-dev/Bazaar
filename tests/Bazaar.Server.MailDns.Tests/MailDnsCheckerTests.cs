using System.Net;

namespace Bazaar.Server.MailDns.Tests;

/// <summary>The rules of doc 22 "DNS checker module (WP10)", each with a positive and a negative case, on a stub resolver (no network).</summary>
public sealed class MailDnsCheckerTests
{
    public const string Domain = "example.com";
    public const string Sender = "_spf.sender.test";

    private static MailDnsRequest Request(
        string? selector = null,
        string? sender = null,
        string? reportAddress = null,
        bool selfHosted = false,
        int? volume = null,
        IPAddress? ip = null) =>
        new(Domain, null, selector, sender, reportAddress, selfHosted, volume, ip);

    private static async Task<DnsCheckResult> Check(DnsCheckKind kind, StubDnsResolver resolver, MailDnsRequest request)
    {
        var report = await new MailDnsChecker(resolver).CheckAsync(request, CancellationToken.None);
        return report[kind];
    }

    private static Task<DnsCheckResult> Spf(StubDnsResolver resolver, string? sender = null) =>
        Check(DnsCheckKind.Spf, resolver, Request(sender: sender));

    // ---- SPF ----

    [Fact]
    public async Task Spf_single_record_with_sender_include_and_dash_all_passes()
    {
        var resolver = new StubDnsResolver().Txt(Domain, $"v=spf1 include:{Sender} -all").Txt(Sender, "v=spf1 ip4:192.0.2.0/24 -all");

        var result = await Spf(resolver, Sender);

        Assert.Equal(DnsCheckStatus.Pass, result.Status);
        Assert.Empty(result.Fix);
    }

    [Fact]
    public async Task Spf_tilde_all_is_a_warning()
    {
        Assert.Equal(DnsCheckStatus.Warn, (await Spf(new StubDnsResolver().Txt(Domain, "v=spf1 ip4:192.0.2.1 ~all"))).Status);
    }

    [Theory]
    [InlineData("v=spf1 ip4:192.0.2.1 +all")]
    [InlineData("v=spf1 ip4:192.0.2.1 all")]
    [InlineData("v=spf1 ip4:192.0.2.1 ?all")]
    [InlineData("v=spf1 ip4:192.0.2.1")]
    public async Task Spf_plus_all_bare_all_question_all_and_no_all_fail(string record)
    {
        Assert.Equal(DnsCheckStatus.Fail, (await Spf(new StubDnsResolver().Txt(Domain, record))).Status);
    }

    [Fact]
    public async Task Spf_two_records_fail_and_no_record_fails()
    {
        var two = new StubDnsResolver().Txt(Domain, "v=spf1 -all", "v=spf1 ip4:192.0.2.1 -all");
        var none = new StubDnsResolver().Txt(Domain, "some other text");

        Assert.Equal(DnsCheckStatus.Fail, (await Spf(two)).Status);
        Assert.Equal(DnsCheckStatus.Fail, (await Spf(none)).Status);
    }

    [Fact]
    public async Task Spf_a_record_that_only_starts_with_the_same_letters_is_not_spf()
    {
        var resolver = new StubDnsResolver().Txt(Domain, "v=spf10 -all");

        Assert.Equal(DnsCheckStatus.Fail, (await Spf(resolver)).Status);
    }

    [Fact]
    public async Task Spf_exactly_ten_lookups_passes_and_eleven_fails()
    {
        string Record(int count) => "v=spf1 " + string.Concat(Enumerable.Repeat("a ", count)) + "-all";

        var ten = await Spf(new StubDnsResolver().Txt(Domain, Record(10)));
        var eleven = await Spf(new StubDnsResolver().Txt(Domain, Record(11)));

        Assert.Equal(DnsCheckStatus.Pass, ten.Status);
        Assert.Equal(DnsCheckStatus.Fail, eleven.Status);
        Assert.Contains("more than 10", eleven.Detail);
    }

    [Fact]
    public async Task Spf_counts_include_mx_ptr_exists_and_redirect_lookups()
    {
        var resolver = new StubDnsResolver()
            .Txt(Domain, "v=spf1 include:a.test mx ptr exists:%{i}.x.test a -all")
            .Txt("a.test", "v=spf1 include:b.test -all")
            .Txt("b.test", "v=spf1 a a a a a -all");

        // include a(1) + include b(2) + 5 a (7) + mx(8) + ptr(9) + exists(10) + a(11).
        var result = await Spf(resolver);

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
    }

    [Fact]
    public async Task Spf_redirect_counts_as_a_lookup_and_its_all_decides_the_verdict()
    {
        var soft = new StubDnsResolver().Txt(Domain, "v=spf1 redirect=other.test").Txt("other.test", "v=spf1 ip4:192.0.2.1 ~all");
        var hard = new StubDnsResolver().Txt(Domain, "v=spf1 redirect=other.test").Txt("other.test", "v=spf1 ip4:192.0.2.1 -all");
        var many = new StubDnsResolver().Txt(Domain, "v=spf1 a a a a a a a a a redirect=other.test").Txt("other.test", "v=spf1 -all");

        Assert.Equal(DnsCheckStatus.Warn, (await Spf(soft)).Status);
        Assert.Equal(DnsCheckStatus.Pass, (await Spf(hard)).Status);
        Assert.Equal(DnsCheckStatus.Pass, (await Spf(many)).Status);
    }

    [Fact]
    public async Task Spf_an_all_inside_an_include_does_not_end_the_record()
    {
        var resolver = new StubDnsResolver().Txt(Domain, $"v=spf1 include:{Sender}").Txt(Sender, "v=spf1 ip4:192.0.2.1 -all");

        Assert.Equal(DnsCheckStatus.Fail, (await Spf(resolver, Sender)).Status);
    }

    [Fact]
    public async Task Spf_include_loop_is_a_fail_and_the_walk_ends()
    {
        var resolver = new StubDnsResolver()
            .Txt(Domain, "v=spf1 include:a.test -all")
            .Txt("a.test", "v=spf1 include:b.test -all")
            .Txt("b.test", "v=spf1 include:a.test -all");

        var result = await Spf(resolver);

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
        Assert.Contains("loops", result.Detail);
        Assert.True(resolver.TxtQueries.Count <= 4);
    }

    [Fact]
    public async Task Spf_a_record_that_includes_the_domain_itself_is_a_loop()
    {
        var resolver = new StubDnsResolver().Txt(Domain, $"v=spf1 include:{Domain} -all");

        Assert.Equal(DnsCheckStatus.Fail, (await Spf(resolver)).Status);
    }

    [Fact]
    public async Task Spf_depth_is_capped_and_the_fetch_count_stays_bounded()
    {
        var resolver = new StubDnsResolver().Txt(Domain, "v=spf1 include:h0.test -all");
        for (var i = 0; i < 40; i++)
        {
            resolver.Txt($"h{i}.test", $"v=spf1 include:h{i + 1}.test -all");
        }

        var result = await Spf(resolver);

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
        Assert.True(resolver.TxtQueries.Count <= 16, $"{resolver.TxtQueries.Count} queries");
    }

    [Fact]
    public async Task Spf_include_that_does_not_exist_fails_and_one_that_times_out_only_warns()
    {
        var missing = new StubDnsResolver().Txt(Domain, "v=spf1 include:gone.test -all");
        var slow = new StubDnsResolver().Txt(Domain, "v=spf1 include:slow.test -all").TxtFails("slow.test", DnsLookupStatus.Timeout);

        Assert.Equal(DnsCheckStatus.Fail, (await Spf(missing)).Status);
        Assert.Equal(DnsCheckStatus.Warn, (await Spf(slow)).Status);
    }

    [Theory]
    [InlineData(DnsLookupStatus.Timeout, DnsCheckStatus.Warn)]
    [InlineData(DnsLookupStatus.ServFail, DnsCheckStatus.Warn)]
    [InlineData(DnsLookupStatus.NxDomain, DnsCheckStatus.Fail)]
    public async Task Spf_resolver_failures_are_results_not_exceptions(DnsLookupStatus status, DnsCheckStatus expected)
    {
        var result = await Spf(new StubDnsResolver().TxtFails(Domain, status));

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task Spf_missing_sender_include_warns_and_a_foreign_include_warns()
    {
        var missing = new StubDnsResolver().Txt(Domain, "v=spf1 ip4:192.0.2.1 -all");
        var foreign = new StubDnsResolver()
            .Txt(Domain, $"v=spf1 include:{Sender} include:other.test -all")
            .Txt(Sender, "v=spf1 -all")
            .Txt("other.test", "v=spf1 -all");

        var noInclude = await Spf(missing, Sender);
        var extra = await Spf(foreign, Sender);

        Assert.Equal(DnsCheckStatus.Warn, noInclude.Status);
        Assert.Contains("include:" + Sender, noInclude.Fix);
        Assert.Equal(DnsCheckStatus.Warn, extra.Status);
    }

    [Fact]
    public async Task Spf_oversized_text_is_cut_and_never_throws()
    {
        var huge = "v=spf1 " + new string('a', 100_000) + " -all";

        var result = await Spf(new StubDnsResolver().Txt(Domain, huge));

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
    }

    [Fact]
    public async Task Spf_dns_text_is_never_echoed_as_markup()
    {
        var resolver = new StubDnsResolver().Txt(Domain, "v=spf1 include:<script>x</script>.test -all");

        var result = await Spf(resolver);

        Assert.DoesNotContain("<", result.Detail + result.Fix);
    }

    // ---- DKIM ----

    private static Task<DnsCheckResult> Dkim(StubDnsResolver resolver, string? selector = "s1") =>
        Check(DnsCheckKind.Dkim, resolver, Request(selector: selector));

    [Fact]
    public async Task Dkim_record_with_key_passes()
    {
        var resolver = new StubDnsResolver().Txt("s1._domainkey." + Domain, "v=DKIM1; k=rsa; p=MIGfMA0GCSqGSIb3DQEBAQUAA4GN");

        Assert.Equal(DnsCheckStatus.Pass, (await Dkim(resolver)).Status);
    }

    [Fact]
    public async Task Dkim_empty_p_is_a_revoked_key_and_fails()
    {
        var resolver = new StubDnsResolver().Txt("s1._domainkey." + Domain, "v=DKIM1; k=rsa; p=");

        var result = await Dkim(resolver);

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
        Assert.Contains("revoked", result.Detail);
    }

    [Fact]
    public async Task Dkim_without_a_selector_is_not_applicable()
    {
        Assert.Equal(DnsCheckStatus.NotApplicable, (await Dkim(new StubDnsResolver(), selector: null)).Status);
    }

    [Theory]
    [InlineData("p=MIGfMA0GCSqGSIb3DQEB")]
    [InlineData("v=DKIM2; p=MIGfMA0GCSqGSIb3DQEB")]
    public async Task Dkim_record_without_v_dkim1_fails(string record)
    {
        var resolver = new StubDnsResolver().Txt("s1._domainkey." + Domain, record);

        Assert.Equal(DnsCheckStatus.Fail, (await Dkim(resolver)).Status);
    }

    [Fact]
    public async Task Dkim_missing_name_fails_and_a_timeout_only_warns()
    {
        var timeout = new StubDnsResolver().TxtFails("s1._domainkey." + Domain, DnsLookupStatus.Timeout);

        Assert.Equal(DnsCheckStatus.Fail, (await Dkim(new StubDnsResolver())).Status);
        Assert.Equal(DnsCheckStatus.Warn, (await Dkim(timeout)).Status);
    }

    [Fact]
    public async Task Dkim_selector_that_is_not_a_dns_label_fails_without_a_query()
    {
        var resolver = new StubDnsResolver();

        var result = await Dkim(resolver, "bad selector;");

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
        Assert.DoesNotContain(resolver.TxtQueries, name => name.Contains("_domainkey", StringComparison.Ordinal));
    }

    // ---- DMARC ----

    private static Task<DnsCheckResult> Dmarc(StubDnsResolver resolver, string? reportAddress = null) =>
        Check(DnsCheckKind.Dmarc, resolver, Request(reportAddress: reportAddress));

    private static StubDnsResolver WithDmarc(params string[] records) => new StubDnsResolver().Txt("_dmarc." + Domain, records);

    [Fact]
    public async Task Dmarc_none_with_rua_mailto_passes_for_a_new_domain()
    {
        Assert.Equal(DnsCheckStatus.Pass, (await Dmarc(WithDmarc("v=DMARC1; p=none; rua=mailto:reports@example.net"))).Status);
    }

    [Fact]
    public async Task Dmarc_none_without_rua_warns_and_names_the_fix()
    {
        var result = await Dmarc(WithDmarc("v=DMARC1; p=none"), "reports@example.net");

        Assert.Equal(DnsCheckStatus.Warn, result.Status);
        Assert.Contains("rua=mailto:reports@example.net", result.Fix);
    }

    [Fact]
    public async Task Dmarc_rua_that_is_not_mailto_does_not_count_as_a_report_address()
    {
        Assert.Equal(DnsCheckStatus.Warn, (await Dmarc(WithDmarc("v=DMARC1; p=none; rua=https://x.test/r"))).Status);
    }

    [Fact]
    public async Task Dmarc_none_with_a_different_report_address_than_configured_warns()
    {
        var resolver = WithDmarc("v=DMARC1; p=none; rua=mailto:other@example.net");

        Assert.Equal(DnsCheckStatus.Warn, (await Dmarc(resolver, "reports@example.net")).Status);
        Assert.Equal(DnsCheckStatus.Pass, (await Dmarc(resolver, "other@example.net")).Status);
    }

    [Fact]
    public async Task Dmarc_no_record_fails_and_the_fix_shows_the_starter_record()
    {
        var result = await Dmarc(new StubDnsResolver(), "reports@example.net");

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
        Assert.Contains("v=DMARC1; p=none; rua=mailto:reports@example.net", result.Fix);
    }

    [Theory]
    [InlineData("v=DMARC1; p=quarantine")]
    [InlineData("v=DMARC1; p=reject; rua=mailto:r@example.net")]
    [InlineData("v=dmarc1; P=Reject")]
    public async Task Dmarc_quarantine_and_reject_pass(string record)
    {
        Assert.Equal(DnsCheckStatus.Pass, (await Dmarc(WithDmarc(record))).Status);
    }

    [Fact]
    public async Task Dmarc_several_records_or_a_bad_policy_fail()
    {
        Assert.Equal(DnsCheckStatus.Fail, (await Dmarc(WithDmarc("v=DMARC1; p=none", "v=DMARC1; p=reject"))).Status);
        Assert.Equal(DnsCheckStatus.Fail, (await Dmarc(WithDmarc("v=DMARC1; p=banana"))).Status);
        Assert.Equal(DnsCheckStatus.Fail, (await Dmarc(WithDmarc("v=DMARC1"))).Status);
        Assert.Equal(DnsCheckStatus.Fail, (await Dmarc(WithDmarc("hello"))).Status);
    }

    [Fact]
    public async Task Dmarc_timeout_warns()
    {
        Assert.Equal(DnsCheckStatus.Warn, (await Dmarc(new StubDnsResolver().TxtFails("_dmarc." + Domain, DnsLookupStatus.ServFail))).Status);
    }

    // ---- PTR ----

    private static Task<DnsCheckResult> Ptr(StubDnsResolver resolver, bool selfHosted = true, IPAddress? ip = null) =>
        Check(DnsCheckKind.Ptr, resolver, Request(selfHosted: selfHosted, ip: ip));

    private static readonly IPAddress _sendingIp = IPAddress.Parse("192.0.2.25");

    [Fact]
    public async Task Ptr_is_not_applicable_when_the_host_is_not_self_hosted()
    {
        Assert.Equal(DnsCheckStatus.NotApplicable, (await Ptr(new StubDnsResolver(), selfHosted: false, ip: _sendingIp)).Status);
    }

    [Fact]
    public async Task Ptr_forward_confirmed_passes()
    {
        var resolver = new StubDnsResolver().Ptr("192.0.2.25", "mail.example.com.").Address("mail.example.com", "192.0.2.25");

        Assert.Equal(DnsCheckStatus.Pass, (await Ptr(resolver, ip: _sendingIp)).Status);
    }

    [Fact]
    public async Task Ptr_name_that_resolves_elsewhere_fails()
    {
        var resolver = new StubDnsResolver().Ptr("192.0.2.25", "mail.example.com").Address("mail.example.com", "192.0.2.99");

        var result = await Ptr(resolver, ip: _sendingIp);

        Assert.Equal(DnsCheckStatus.Fail, result.Status);
        Assert.Contains("forward-confirmed", result.Detail);
    }

    [Fact]
    public async Task Ptr_missing_record_fails_and_a_timeout_only_warns()
    {
        var timeout = new StubDnsResolver().PtrFails("192.0.2.25", DnsLookupStatus.Timeout);

        Assert.Equal(DnsCheckStatus.Fail, (await Ptr(new StubDnsResolver(), ip: _sendingIp)).Status);
        Assert.Equal(DnsCheckStatus.Warn, (await Ptr(timeout, ip: _sendingIp)).Status);
    }

    [Fact]
    public async Task Ptr_finds_the_sending_address_from_the_mx_host_when_none_is_given()
    {
        var resolver = new StubDnsResolver()
            .Mx(Domain, (10, "mx.example.com."))
            .Address("mx.example.com", "192.0.2.25")
            .Ptr("192.0.2.25", "mx.example.com")
            .Ptr("192.0.2.26", "other.example.com");

        Assert.Equal(DnsCheckStatus.Pass, (await Ptr(resolver)).Status);
    }

    [Fact]
    public async Task Ptr_falls_back_to_the_domain_address_and_warns_when_nothing_is_found()
    {
        var viaDomain = new StubDnsResolver().Address(Domain, "192.0.2.25").Ptr("192.0.2.25", Domain);

        Assert.Equal(DnsCheckStatus.Pass, (await Ptr(viaDomain)).Status);
        Assert.Equal(DnsCheckStatus.Warn, (await Ptr(new StubDnsResolver())).Status);
    }

    [Fact]
    public async Task Ptr_every_sending_address_must_confirm()
    {
        var resolver = new StubDnsResolver()
            .Mx(Domain, (10, "mx.example.com"))
            .Address("mx.example.com", "192.0.2.25", "192.0.2.26")
            .Ptr("192.0.2.25", "mx.example.com")
            .Ptr("192.0.2.26", "elsewhere.example.com");

        Assert.Equal(DnsCheckStatus.Fail, (await Ptr(resolver)).Status);
    }

    // ---- BulkSender ----

    private static Task<DnsCheckResult> Bulk(int? volume) => Check(DnsCheckKind.BulkSender, new StubDnsResolver(), Request(volume: volume));

    [Fact]
    public async Task Bulk_sender_warns_over_5000_a_day_and_passes_at_5000()
    {
        Assert.Equal(DnsCheckStatus.Warn, (await Bulk(5001)).Status);
        Assert.Equal(DnsCheckStatus.Pass, (await Bulk(5000)).Status);
        Assert.Equal(DnsCheckStatus.Pass, (await Bulk(0)).Status);
    }

    [Fact]
    public async Task Bulk_sender_without_a_volume_is_not_applicable()
    {
        Assert.Equal(DnsCheckStatus.NotApplicable, (await Bulk(null)).Status);
    }

    // ---- Report-level behaviour ----

    [Fact]
    public async Task Report_holds_one_result_per_check_kind_in_order()
    {
        var report = await new MailDnsChecker(new StubDnsResolver()).CheckAsync(Request(), CancellationToken.None);

        Assert.Equal(
            [DnsCheckKind.Spf, DnsCheckKind.Dkim, DnsCheckKind.Dmarc, DnsCheckKind.Ptr, DnsCheckKind.BulkSender],
            report.Results.Select(r => r.Kind));
        Assert.Equal(Domain, report.Domain);
    }

    [Fact]
    public async Task A_resolver_that_throws_gives_warnings_not_an_exception()
    {
        var resolver = new StubDnsResolver { ThrowOnEveryQuery = true };

        var report = await new MailDnsChecker(resolver).CheckAsync(Request(selector: "s1", selfHosted: true, ip: _sendingIp), CancellationToken.None);

        Assert.Equal(DnsCheckStatus.Warn, report[DnsCheckKind.Spf].Status);
        Assert.Equal(DnsCheckStatus.Warn, report[DnsCheckKind.Dkim].Status);
        Assert.Equal(DnsCheckStatus.Warn, report[DnsCheckKind.Dmarc].Status);
        Assert.Equal(DnsCheckStatus.Warn, report[DnsCheckKind.Ptr].Status);
        Assert.DoesNotContain("stub resolver failure", string.Join(' ', report.Results.Select(r => r.Detail)));
    }

    [Fact]
    public async Task Caller_cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var resolver = new CancellingResolver(cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new MailDnsChecker(resolver).CheckAsync(Request(), cts.Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a domain")]
    [InlineData("-bad-.example.com")]
    public async Task An_invalid_domain_fails_every_applicable_check_without_a_query(string domain)
    {
        var resolver = new StubDnsResolver();

        var report = await new MailDnsChecker(resolver).CheckAsync(new MailDnsRequest(domain, ExpectedDailyVolume: 10), CancellationToken.None);

        Assert.Equal(DnsCheckStatus.Fail, report[DnsCheckKind.Spf].Status);
        Assert.Equal(DnsCheckStatus.Fail, report[DnsCheckKind.Dmarc].Status);
        Assert.Equal(DnsCheckStatus.NotApplicable, report[DnsCheckKind.Dkim].Status);
        Assert.Empty(resolver.TxtQueries);
    }

    [Fact]
    public async Task The_domain_is_normalized_before_use()
    {
        var resolver = new StubDnsResolver().Txt(Domain, "v=spf1 -all");

        var report = await new MailDnsChecker(resolver).CheckAsync(new MailDnsRequest("  Example.COM. "), CancellationToken.None);

        Assert.Equal(DnsCheckStatus.Pass, report[DnsCheckKind.Spf].Status);
        Assert.Equal(Domain, report.Domain);
    }

    private sealed class CancellingResolver(CancellationTokenSource source) : IDnsResolver
    {
        public Task<DnsLookup<string>> TxtAsync(string name, CancellationToken cancellationToken)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(DnsLookup.Ok<string>([]));
        }

        public Task<DnsLookup<string>> PtrAsync(IPAddress address, CancellationToken cancellationToken) => TxtAsync("", cancellationToken);

        public Task<DnsLookup<IPAddress>> AddressesAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(DnsLookup.Ok<IPAddress>([]));

        public Task<DnsLookup<DnsMxRecord>> MxAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(DnsLookup.Ok<DnsMxRecord>([]));
    }
}
