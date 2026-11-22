using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.Browser.Tests;

public sealed class ChromiumCommandLineTests
{
    private static readonly BrowserEgressEndpoint _egress = new("egress.internal", 3128);

    private static IReadOnlyList<string> Build(
        BrowserProfileClass profileClass = BrowserProfileClass.Account,
        BrowserDisplayMode display = BrowserDisplayMode.Headless,
        BrowserGuestOs os = BrowserGuestOs.Linux,
        string profileDirectory = "/var/lib/bazaar/browser/p1",
        BrowserEgressEndpoint? egress = null,
        string? startUrl = null)
        => ChromiumCommandLine.Build(profileClass, display, os, profileDirectory, egress ?? _egress, startUrl);

    private static readonly string[] _expectedAnonymousHeadless =
    [
                "--user-data-dir=/var/lib/bazaar/browser/anon",
                "--proxy-server=http://egress.internal:3128",
                "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE egress.internal",
                "--remote-debugging-pipe",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-sync",
                "--disable-background-networking",
                "--disable-extensions",
                "--disable-component-update",
                "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
                "--headless=new",
                "--incognito",
                "https://example.com/",

    ];

    [Fact]
    public void Sets_user_data_dir_from_profile_directory()
    {
        var args = Build(profileDirectory: "/var/lib/bazaar/browser/p1");

        Assert.Contains("--user-data-dir=/var/lib/bazaar/browser/p1", args);
    }

    [Fact]
    public void Routes_all_traffic_through_the_egress_proxy()
    {
        var args = Build();

        Assert.Contains("--proxy-server=http://egress.internal:3128", args);
    }

    [Fact]
    public void Resolves_no_dns_locally_except_the_egress_host()
    {
        var args = Build();

        Assert.Contains("--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE egress.internal", args);
    }

    [Theory]
    [InlineData("--remote-debugging-pipe")]
    [InlineData("--no-first-run")]
    [InlineData("--no-default-browser-check")]
    [InlineData("--disable-sync")]
    [InlineData("--disable-background-networking")]
    [InlineData("--disable-extensions")]
    [InlineData("--disable-component-update")]
    [InlineData("--force-webrtc-ip-handling-policy=disable_non_proxied_udp")]
    public void Includes_required_fixed_flags(string flag)
    {
        Assert.Contains(flag, Build());
    }

    [Fact]
    public void Headless_adds_new_headless_flag()
    {
        var args = Build(display: BrowserDisplayMode.Headless);

        Assert.Contains("--headless=new", args);
    }

    [Fact]
    public void Headed_adds_no_headless_flag()
    {
        var args = Build(display: BrowserDisplayMode.Headed);

        Assert.DoesNotContain(args, a => a.StartsWith("--headless", StringComparison.Ordinal));
    }

    [Fact]
    public void Anonymous_profile_runs_incognito()
    {
        var args = Build(profileClass: BrowserProfileClass.Anonymous);

        Assert.Contains("--incognito", args);
    }

    [Fact]
    public void Account_profile_does_not_run_incognito()
    {
        var args = Build(profileClass: BrowserProfileClass.Account);

        Assert.DoesNotContain("--incognito", args);
    }

    [Theory]
    [InlineData(BrowserGuestOs.Linux)]
    [InlineData(BrowserGuestOs.Windows)]
    public void Guest_os_does_not_change_the_arguments(BrowserGuestOs os)
    {
        Assert.Equal(Build(os: BrowserGuestOs.Linux), Build(os: os));
    }

    [Theory]
    [InlineData("--no-sandbox")]
    [InlineData("--remote-debugging-port")]
    [InlineData("--disable-web-security")]
    public void Never_emits_forbidden_flags(string forbidden)
    {
        foreach (var profileClass in Enum.GetValues<BrowserProfileClass>())
            foreach (var display in Enum.GetValues<BrowserDisplayMode>())
                foreach (var os in Enum.GetValues<BrowserGuestOs>())
                {
                    var args = Build(profileClass, display, os, startUrl: "https://example.com/");
                    Assert.DoesNotContain(args, a => a == forbidden || a.StartsWith(forbidden + "=", StringComparison.Ordinal));
                }
    }

    [Fact]
    public void Start_url_is_the_last_argument()
    {
        var args = Build(startUrl: "https://example.com/path?q=1");

        Assert.Equal("https://example.com/path?q=1", args[^1]);
    }

    [Fact]
    public void Without_start_url_no_url_argument_is_added()
    {
        var args = Build(startUrl: null);

        Assert.Equal("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", args.Single(a => a.StartsWith("--force-webrtc", StringComparison.Ordinal)));
        Assert.DoesNotContain(args, a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("--user-data-dir=", args[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com/")]
    [InlineData("https://example.com/")]
    public void Accepts_absolute_http_and_https_start_urls(string url)
    {
        Assert.Equal(url, Build(startUrl: url)[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com")]
    [InlineData("/relative/path")]
    [InlineData("file:///etc/passwd")]
    [InlineData("chrome://settings")]
    [InlineData("javascript:alert(1)")]
    [InlineData("--remote-debugging-port=9222")]
    public void Rejects_start_url_that_is_not_absolute_http_or_https(string url)
    {
        Assert.Throws<ArgumentException>("startUrl", () => Build(startUrl: url));
    }

    [Fact]
    public void Rejects_empty_profile_directory()
    {
        Assert.Throws<ArgumentException>("profileDirectory", () => Build(profileDirectory: ""));
    }

    [Fact]
    public void Rejects_whitespace_profile_directory()
    {
        Assert.Throws<ArgumentException>("profileDirectory", () => Build(profileDirectory: "   "));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Rejects_egress_port_outside_range(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>("egress", () => Build(egress: new BrowserEgressEndpoint("egress.internal", port)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void Accepts_egress_port_at_range_edges(int port)
    {
        var args = Build(egress: new BrowserEgressEndpoint("egress.internal", port));

        Assert.Contains($"--proxy-server=http://egress.internal:{port}", args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("egress host")]
    [InlineData("egress.internal,MAP evil")]
    [InlineData("egress.internal\"")]
    [InlineData("[::1]")]
    [InlineData("egress.internal/x")]
    public void Rejects_egress_host_that_could_alter_the_command_line(string host)
    {
        Assert.Throws<ArgumentException>(nameof(host), () => Build(egress: new BrowserEgressEndpoint(host, 3128)));
    }

    [Fact]
    public void Rejects_null_egress()
    {
        Assert.Throws<ArgumentNullException>("egress", () => ChromiumCommandLine.Build(
            BrowserProfileClass.Account, BrowserDisplayMode.Headless, BrowserGuestOs.Linux, "/p", null!, null));
    }

    [Fact]
    public void Rejects_undefined_enum_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>("profileClass", () =>
            ChromiumCommandLine.Build((BrowserProfileClass)99, BrowserDisplayMode.Headless, BrowserGuestOs.Linux, "/p", _egress, null));
        Assert.Throws<ArgumentOutOfRangeException>("display", () =>
            ChromiumCommandLine.Build(BrowserProfileClass.Account, (BrowserDisplayMode)99, BrowserGuestOs.Linux, "/p", _egress, null));
        Assert.Throws<ArgumentOutOfRangeException>("os", () =>
            ChromiumCommandLine.Build(BrowserProfileClass.Account, BrowserDisplayMode.Headless, (BrowserGuestOs)99, "/p", _egress, null));
    }

    [Fact]
    public void Full_argument_vector_for_anonymous_headless_linux_session()
    {
        var args = Build(
            profileClass: BrowserProfileClass.Anonymous,
            display: BrowserDisplayMode.Headless,
            os: BrowserGuestOs.Linux,
            profileDirectory: "/var/lib/bazaar/browser/anon",
            startUrl: "https://example.com/");

        Assert.Equal(_expectedAnonymousHeadless, args);
    }
}
