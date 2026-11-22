using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Tests;

public class OptionsValidationTests
{
    private static IReadOnlyList<string> Check(Action<BrowserPoolOptions> change)
    {
        var o = new BrowserPoolOptions();
        change(o);
        return BrowserPoolOptionsValidator.Check(o);
    }

    [Fact]
    public void Defaults_are_valid() => Assert.Empty(BrowserPoolOptionsValidator.Check(new BrowserPoolOptions()));

    [Fact]
    public void Default_values_match_the_design()
    {
        var o = new BrowserPoolOptions();

        Assert.True(o.Enabled);
        Assert.Equal(32, o.MaxSessions);
        Assert.Equal(4, o.MaxSessionsPerAgent);
        Assert.Equal(20000, o.SlotUidBase);
        Assert.Equal("/var/lib/bazaar-pool", o.SlotRoot);
        Assert.Equal("/usr/bin/chromium", o.ChromiumPath);
        Assert.Equal("/usr/bin/bwrap", o.BwrapPath);
        Assert.Equal("/usr/bin/setpriv", o.SetprivPath);
        Assert.Equal("/usr/bin/cage", o.CagePath);
        Assert.Equal(TimeSpan.FromHours(2), o.SessionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(20), o.IdleTimeout);
        Assert.Equal(1024, o.MemoryMaxMb);
        Assert.True(o.UseSystemdRun);
        Assert.Equal(20031, o.MaxUid);
    }

    [Theory]
    [InlineData(9999)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Uid_base_below_10000_is_refused(int value) => Assert.NotEmpty(Check(o => o.SlotUidBase = value));

    [Fact]
    public void Uid_range_must_fit_below_60000()
    {
        Assert.NotEmpty(Check(o => { o.SlotUidBase = 59990; o.MaxSessions = 10; }));
        Assert.Empty(Check(o => { o.SlotUidBase = 59967; o.MaxSessions = 32; }));
        Assert.NotEmpty(Check(o => { o.SlotUidBase = 59968; o.MaxSessions = 32; }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public void Max_sessions_out_of_range_is_refused(int value) => Assert.NotEmpty(Check(o => o.MaxSessions = value));

    [Fact]
    public void Per_agent_limit_may_not_exceed_the_pool()
    {
        Assert.NotEmpty(Check(o => { o.MaxSessions = 2; o.MaxSessionsPerAgent = 3; }));
        Assert.NotEmpty(Check(o => o.MaxSessionsPerAgent = 0));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/var")]
    [InlineData("var/lib/pool")]
    [InlineData("")]
    [InlineData("/var/lib/pool/")]
    [InlineData("/var//lib/pool")]
    [InlineData("/var/lib/../pool")]
    [InlineData("/var/lib/./pool")]
    [InlineData("/var/lib/my pool")]
    [InlineData("/var/lib/pool\t")]
    [InlineData("/var/lib/pool;rm")]
    [InlineData("/var/lib/$HOME")]
    [InlineData("/usr/lib/pool")]
    [InlineData("/etc/pool")]
    [InlineData("/root/pool")]
    [InlineData("/proc/pool")]
    public void Bad_slot_roots_are_refused(string root) => Assert.NotEmpty(Check(o => o.SlotRoot = root));

    [Theory]
    [InlineData("/var/lib/bazaar-pool")]
    [InlineData("/srv/pool")]
    [InlineData("/tmp/bazaar-test/pool")]
    [InlineData("/data/pool_1.d")]
    public void Good_slot_roots_are_accepted(string root) => Assert.Empty(Check(o => o.SlotRoot = root));

    [Theory]
    [InlineData("ChromiumPath")]
    [InlineData("BwrapPath")]
    [InlineData("SetprivPath")]
    [InlineData("CagePath")]
    public void Tool_paths_are_checked(string which)
    {
        foreach (var bad in new[] { "relative/bin", "-x", "/bin/a b", "/bin/../x", "/bin/x\n", "" })
        {
            var errors = Check(o =>
            {
                switch (which)
                {
                    case "ChromiumPath": o.ChromiumPath = bad; break;
                    case "BwrapPath": o.BwrapPath = bad; break;
                    case "SetprivPath": o.SetprivPath = bad; break;
                    default: o.CagePath = bad; break;
                }
            });
            Assert.NotEmpty(errors);
        }
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-01:00:00")]
    [InlineData("25:00:00")]
    public void Session_lifetime_is_bounded(string value) => Assert.NotEmpty(Check(o => o.SessionLifetime = TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Idle_and_open_timeouts_must_be_positive()
    {
        Assert.NotEmpty(Check(o => o.IdleTimeout = TimeSpan.Zero));
        Assert.NotEmpty(Check(o => o.OpenTimeout = TimeSpan.Zero));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(2_000_000)]
    public void Memory_limit_out_of_range_is_refused(int mb) => Assert.NotEmpty(Check(o => o.MemoryMaxMb = mb));

    [Theory]
    [InlineData(0)]
    [InlineData(128)]
    [InlineData(4096)]
    public void Memory_limit_in_range_or_zero_is_accepted(int mb) => Assert.Empty(Check(o => o.MemoryMaxMb = mb));

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(100_001)]
    public void Cpu_quota_out_of_range_is_refused(int percent) => Assert.NotEmpty(Check(o => o.CpuQuotaPercent = percent));

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(200)]
    [InlineData(3200)]
    public void Cpu_quota_in_range_or_zero_is_accepted(int percent) => Assert.Empty(Check(o => o.CpuQuotaPercent = percent));

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65_537)]
    public void Task_limit_out_of_range_is_refused(int tasks) => Assert.NotEmpty(Check(o => o.TasksMax = tasks));

    [Theory]
    [InlineData(64)]
    [InlineData(512)]
    [InlineData(65_536)]
    public void Task_limit_in_range_is_accepted(int tasks) => Assert.Empty(Check(o => o.TasksMax = tasks));

    [Fact]
    public void Cpu_quota_and_task_limit_have_safe_defaults()
    {
        var o = new BrowserPoolOptions();

        Assert.Equal(200, o.CpuQuotaPercent);
        Assert.Equal(512, o.TasksMax);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/root")]
    [InlineData("/root/secret-dir")]
    [InlineData("/home")]
    [InlineData("/home/x")]
    [InlineData("/var")]
    [InlineData("/var/lib")]
    [InlineData("/run")]
    [InlineData("/run/user")]
    [InlineData("/etc")]
    [InlineData("/proc")]
    [InlineData("/tmp")]
    [InlineData("/var/lib/bazaar-pool")]
    [InlineData("/var/lib/bazaar-pool/slot-1")]
    [InlineData("rel")]
    [InlineData("/a b")]
    public void Bad_extra_binds_are_refused(string bind) => Assert.NotEmpty(Check(o => o.ExtraReadOnlyBinds = [bind]));

    [Fact]
    public void Good_extra_bind_is_accepted() => Assert.Empty(Check(o => o.ExtraReadOnlyBinds = ["/opt/pw-browsers"]));

    [Fact]
    public void Validator_reports_failure_through_the_options_interface()
    {
        var result = new BrowserPoolOptionsValidator().Validate(null, new BrowserPoolOptions { SlotUidBase = 5 });

        Assert.True(result.Failed);
    }

    [Fact]
    public void The_service_refuses_to_start_with_invalid_options()
    {
        Assert.Throws<ArgumentException>(() => new Rig(o => o.SlotRoot = "/var"));
    }
}

public class RequestValidationTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("agent-1")]
    [InlineData("Agent_Two")]
    [InlineData("0abc")]
    [InlineData("abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void Good_agent_ids_are_accepted(string id) => Assert.True(PoolRequestValidator.IsValidAgentId(id));

    [Theory]
    [InlineData("")]
    [InlineData("-lead")]
    [InlineData("_lead")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("new\nline")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("abbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("é")]
    [InlineData("a$b")]
    public void Bad_agent_ids_are_refused(string id) => Assert.False(PoolRequestValidator.IsValidAgentId(id));

    [Fact]
    public void Null_agent_id_is_refused() => Assert.False(PoolRequestValidator.IsValidAgentId(null));

    [Theory]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("HTTPS://EXAMPLE.com/A?b=c#d", "https://example.com/A?b=c#d")]
    [InlineData("http://example.com:8080/x", "http://example.com:8080/x")]
    [InlineData("http://[2001:db8::1]/", "http://[2001:db8::1]/")]
    public void Good_urls_are_normalised(string url, string expected)
    {
        Assert.Null(PoolRequestValidator.ValidateUrl(url, out var normalised));
        Assert.Equal(expected, normalised);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-evil")]
    [InlineData("--no-sandbox")]
    [InlineData("--proxy-server=http://x")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("chrome://settings")]
    [InlineData("about:blank")]
    [InlineData("http://")]
    [InlineData("http://user@example.com/")]
    [InlineData("http://example.com/\u0000")]
    [InlineData("http://example.com/\n--no-sandbox")]
    [InlineData("http://example.com/\r")]
    [InlineData("http://example.com/\t")]
    [InlineData("http://example.com/\u007f")]
    public void Bad_urls_are_refused(string url) => Assert.NotNull(PoolRequestValidator.ValidateUrl(url, out _));

    [Fact]
    public void Url_length_limit_is_2048()
    {
        var ok = "http://example.com/" + new string('a', 2048 - "http://example.com/".Length);
        var tooLong = ok + "a";

        Assert.Null(PoolRequestValidator.ValidateUrl(ok, out _));
        Assert.NotNull(PoolRequestValidator.ValidateUrl(tooLong, out _));
    }

    [Theory]
    [InlineData(320, 240, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(1280, 800, true)]
    [InlineData(319, 800, false)]
    [InlineData(3841, 800, false)]
    [InlineData(1280, 239, false)]
    [InlineData(1280, 2161, false)]
    [InlineData(-1, -1, false)]
    [InlineData(0, 0, false)]
    public void Window_size_range(int w, int h, bool valid) =>
        Assert.Equal(valid, PoolRequestValidator.Validate(new PoolSessionRequest("a", Width: w, Height: h), out _) is null);

    [Fact]
    public void Unknown_display_mode_is_refused() =>
        Assert.NotNull(PoolRequestValidator.Validate(new PoolSessionRequest("a", (PoolDisplayMode)7), out _));

    [Fact]
    public void Missing_url_means_about_blank()
    {
        Assert.Null(PoolRequestValidator.Validate(new PoolSessionRequest("a"), out var url));
        Assert.Equal("about:blank", url);
    }

    [Fact]
    public void Null_request_is_refused() => Assert.NotNull(PoolRequestValidator.Validate(null, out _));

    [Theory]
    [InlineData("10.0.0.1", 3128, true)]
    [InlineData("127.0.0.1", 8080, true)]
    [InlineData("192.168.1.20", 1, true)]
    [InlineData("10.0.0.1", 65535, true)]
    [InlineData("proxy.example.com", 3128, false)]
    [InlineData("localhost", 3128, false)]
    [InlineData("10.1", 3128, false)]
    [InlineData("010.0.0.1", 3128, false)]
    [InlineData("::1", 3128, false)]
    [InlineData("0.0.0.0", 3128, false)]
    [InlineData("224.0.0.1", 3128, false)]
    [InlineData("255.255.255.255", 3128, false)]
    [InlineData("10.0.0.1", 0, false)]
    [InlineData("10.0.0.1", 65536, false)]
    [InlineData("", 3128, false)]
    public void Endpoint_validation(string host, int port, bool valid) =>
        Assert.Equal(valid, PoolRequestValidator.ValidateEndpoint(new PoolEgressEndpoint(host, port)) is null);

    [Fact]
    public void Null_endpoint_is_refused() => Assert.NotNull(PoolRequestValidator.ValidateEndpoint(null));
}

public class NftablesTests
{
    [Fact]
    public void Ruleset_text_is_exact()
    {
        var text = NftablesRules.Render(
            [new PoolFirewallRule(20002, "10.0.0.2", 3129), new PoolFirewallRule(20000, "10.0.0.1", 3128)],
            20000,
            20031);

        const string expected =
            "table inet bazaar_pool\n"
            + "delete table inet bazaar_pool\n"
            + "table inet bazaar_pool {\n"
            + "  chain output {\n"
            + "    type filter hook output priority 0; policy accept;\n"
            + "    meta skuid 20000 ip daddr 10.0.0.1 tcp dport 3128 accept\n"
            + "    meta skuid 20002 ip daddr 10.0.0.2 tcp dport 3129 accept\n"
            + "    meta skuid >= 20000 meta skuid <= 20031 drop\n"
            + "  }\n"
            + "}\n";
        Assert.Equal(expected, text);
    }

    [Fact]
    public void With_no_sessions_everything_from_slot_users_is_dropped()
    {
        var text = NftablesRules.Render([], 20000, 20031);

        Assert.DoesNotContain("accept\n", text.Replace("policy accept;", string.Empty, StringComparison.Ordinal));
        Assert.Contains("meta skuid >= 20000 meta skuid <= 20031 drop", text);
    }

    [Fact]
    public void Drop_rule_comes_after_every_accept_rule()
    {
        var text = NftablesRules.Render([new PoolFirewallRule(20001, "10.0.0.1", 3128)], new BrowserPoolOptions());

        Assert.True(text.IndexOf("accept\n", StringComparison.Ordinal) < text.IndexOf(" drop\n", StringComparison.Ordinal));
        Assert.Contains("meta skuid <= 20031 drop", text);
    }

    [Theory]
    [InlineData("proxy.example.com")]
    [InlineData("localhost")]
    [InlineData("10.0.0.1 accept")]
    [InlineData("::1")]
    [InlineData("10.1")]
    [InlineData("")]
    public void Hostnames_and_junk_are_refused(string host)
    {
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([new PoolFirewallRule(20000, host, 3128)], 20000, 20031));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Bad_ports_are_refused(int port)
    {
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([new PoolFirewallRule(20000, "10.0.0.1", port)], 20000, 20031));
    }

    [Fact]
    public void Uid_outside_the_range_is_refused()
    {
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([new PoolFirewallRule(19999, "10.0.0.1", 3128)], 20000, 20031));
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([new PoolFirewallRule(20032, "10.0.0.1", 3128)], 20000, 20031));
    }

    [Fact]
    public void Two_rules_for_one_uid_are_refused()
    {
        Assert.Throws<ArgumentException>(() => NftablesRules.Render(
            [new PoolFirewallRule(20000, "10.0.0.1", 3128), new PoolFirewallRule(20000, "10.0.0.2", 3128)], 20000, 20031));
    }

    [Theory]
    [InlineData(9999, 20031)]
    [InlineData(20031, 20000)]
    [InlineData(20000, 60000)]
    public void Bad_uid_range_is_refused(int first, int last) => Assert.Throws<ArgumentException>(() => NftablesRules.Render([], first, last));
}
