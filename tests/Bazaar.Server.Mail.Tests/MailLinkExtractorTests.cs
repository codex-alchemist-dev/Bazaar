using Bazaar.Server.Mail.Parsing;

namespace Bazaar.Server.Mail.Tests;

public sealed class MailLinkExtractorTests
{
    private static readonly string[] _protected = ["example.test"];

    private static ParsedMail Mail(string? text = null, string? html = null) =>
        new("id@example.test", "sender@example.test", "Subject", text, html, [], false, 1, 1, new Dictionary<string, string>());

    private static MailLinkReport Extract(ParsedMail mail, MailLimits? limits = null) =>
        MailLinkExtractor.Extract(mail, limits ?? MailLimits.Defaults, _protected);

    [Fact]
    public void Finds_text_and_html_links_deduplicated_in_first_seen_order()
    {
        var report = Extract(Mail(
            "See https://www.example.invalid/a and (https://www.example.invalid/b).",
            "<a href=\"https://WWW.example.invalid:443/a\">x</a><a href=\"http://www.example.invalid/c\">y</a>"));

        Assert.Equal(
            ["https://www.example.invalid/a", "https://www.example.invalid/b", "http://www.example.invalid/c"],
            report.Links.Select(l => l.Url));
        Assert.False(report.Truncated);
    }

    [Fact]
    public void Ignores_non_http_schemes_and_junk()
    {
        var report = Extract(Mail(
            "ftp://files.example.invalid/x javascript:alert(1)",
            "<a href=\"mailto:a@example.invalid\">m</a><a href=\"javascript:void(0)\">j</a><a href=\"/relative\">r</a><a>no href</a>"));

        Assert.Empty(report.Links);
    }

    [Fact]
    public void Strips_tracking_fragment_but_keeps_query_and_other_fragments()
    {
        var report = Extract(Mail(
            "https://example.invalid/p?utm_source=a&id=7#utm_campaign=x https://example.invalid/q#section-2 https://example.invalid/r#"));

        Assert.Equal(
            ["https://example.invalid/p?utm_source=a&id=7", "https://example.invalid/q#section-2", "https://example.invalid/r"],
            report.Links.Select(l => l.Url));
    }

    [Fact]
    public void Flags_display_text_naming_another_host()
    {
        var report = Extract(Mail(html: """
            <a href="https://evil.example.invalid/login">https://bank.example.invalid</a>
            <a href="https://ok.example.invalid/x">ok.example.invalid</a>
            <a href="https://prose.example.invalid/x">Click here to continue</a>
            """));

        Assert.True(report.Links.Single(l => l.Host == "evil.example.invalid").DisplayTextMismatch);
        Assert.False(report.Links.Single(l => l.Host == "ok.example.invalid").DisplayTextMismatch);
        Assert.False(report.Links.Single(l => l.Host == "prose.example.invalid").DisplayTextMismatch);
    }

    [Fact]
    public void Mismatch_survives_deduplication_with_a_plain_occurrence()
    {
        var report = Extract(Mail(
            "https://evil.example.invalid/x",
            "<a href=\"https://evil.example.invalid/x\">bank.example.invalid</a>"));

        var link = Assert.Single(report.Links);
        Assert.True(link.DisplayTextMismatch);
    }

    [Fact]
    public void Flags_idn_and_punycode_hosts()
    {
        var report = Extract(Mail("https://xn--bcher-kva.example.invalid/ https://bücher.example.invalid/x https://plain.example.invalid/"));

        Assert.True(report.Links[0].IsInternationalized);
        Assert.True(report.Links[1].IsInternationalized);
        Assert.Equal("xn--bcher-kva.example.invalid", report.Links[1].Host);
        Assert.False(report.Links[2].IsInternationalized);
    }

    [Fact]
    public void Flags_lookalike_of_protected_domain_but_not_the_domain_itself()
    {
        var report = Extract(Mail("https://examp1e.test/a https://example.test/b https://mail.example.test/c"));

        Assert.True(report.Links.Single(l => l.Host == "examp1e.test").IsLookalike);
        Assert.False(report.Links.Single(l => l.Host == "example.test").IsLookalike);
        Assert.False(report.Links.Single(l => l.Host == "mail.example.test").IsLookalike);
    }

    [Fact]
    public void Flags_user_info()
    {
        var report = Extract(Mail("https://example.test@host.example.invalid/x"));

        var link = Assert.Single(report.Links);
        Assert.True(link.HasUserInfo);
        Assert.Equal("host.example.invalid", link.Host);
    }

    [Fact]
    public void Caps_count_and_reports_truncation()
    {
        var text = string.Join(' ', Enumerable.Range(0, 10).Select(i => $"https://example.invalid/{i}"));

        var report = Extract(Mail(text), new MailLimits { MaxLinks = 3 });

        Assert.Equal(3, report.Links.Count);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Drops_and_counts_links_over_the_length_cap()
    {
        var report = Extract(
            Mail("https://example.invalid/" + new string('a', 100) + " https://example.invalid/short"),
            new MailLimits { MaxLinkLength = 50 });

        Assert.Equal(["https://example.invalid/short"], report.Links.Select(l => l.Url));
        Assert.Equal(1, report.DroppedTooLong);
    }

    [Fact]
    public void Is_deterministic_and_reports_no_body_text()
    {
        var mail = Mail("Secret prose here https://example.invalid/x", "<a href=\"https://example.invalid/y\">private anchor words</a>");

        var first = Extract(mail);
        var second = Extract(mail);

        Assert.Equal(first.Links, second.Links);
        Assert.DoesNotContain(first.Links, l => l.Url.Contains("prose", StringComparison.Ordinal) || l.Url.Contains("anchor", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(ExtractedLink).GetProperties(),
            p => p.Name.Contains("Text", StringComparison.Ordinal) && p.Name != nameof(ExtractedLink.DisplayTextMismatch));
    }

    [Fact]
    public void Empty_mail_yields_no_links()
    {
        var report = Extract(Mail());

        Assert.Empty(report.Links);
        Assert.Equal(0, report.DroppedTooLong);
    }

    [Fact]
    public void Link_limits_must_be_valid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MailLimits { MaxLinks = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MailLimits { MaxLinkLength = 0 }.Validate());
    }
}
