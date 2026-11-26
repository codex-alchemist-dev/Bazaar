using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Tests;

public class PoolCommandLineTests
{
    private static readonly PoolEgressEndpoint _egress = new("10.0.0.1", 3128);

    /// <summary>Options for the tests written for the shared namespace (the pre-netns shape of every list).</summary>
    private static BrowserPoolOptions Shared() => new() { NetworkIsolation = NetworkIsolation.SharedWithUidRule };

    private static PoolLaunchSpec Build(BrowserPoolOptions? o = null, int slot = 3, PoolSessionRequest? r = null, PoolEgressEndpoint? e = null) =>
        PoolCommandLine.Build(o ?? Shared(), "sess1", slot, r ?? new PoolSessionRequest("agent-1"), e ?? _egress);

    private static readonly string[] _sandbox =
    [
        "/usr/bin/setpriv", "--reuid", "20003", "--regid", "20003", "--clear-groups", "--no-new-privs", "--inh-caps=-all", "--bounding-set=-all", "--",
        "/usr/bin/bwrap",
        "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-cgroup-try", "--die-with-parent", "--new-session", "--clearenv",
        "--hostname", "pool",
        "--setenv", "HOME", "/home/browser", "--setenv", "XDG_RUNTIME_DIR", "/run/user/browser", "--setenv", "PATH", "/usr/bin:/bin", "--setenv", "LANG", "C.UTF-8",
    ];

    private static readonly string[] _mounts =
    [
        "--ro-bind", "/usr", "/usr",
        "--symlink", "usr/bin", "/bin", "--symlink", "usr/lib", "/lib", "--symlink", "usr/lib64", "/lib64", "--symlink", "usr/sbin", "/sbin",
        "--ro-bind-try", "/etc/fonts", "/etc/fonts", "--ro-bind-try", "/etc/ssl", "/etc/ssl", "--ro-bind-try", "/etc/ca-certificates", "/etc/ca-certificates",
        "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
        "--bind", "/var/lib/bazaar-pool/slot-3/profile", "/home/browser", "--bind", "/var/lib/bazaar-pool/slot-3/run", "/run/user/browser",
        "--chdir", "/home/browser",
    ];

    private static readonly string[] _chromiumCommon =
    [
        "/usr/bin/chromium", "--user-data-dir=/home/browser/profile", "--no-first-run", "--no-default-browser-check", "--disable-sync",
        "--disable-background-networking", "--disable-component-update", "--disable-breakpad", "--password-store=basic",
        "--disable-features=Translate,MediaRouter", "--proxy-server=http://10.0.0.1:3128", "--proxy-bypass-list=<-loopback>",
        "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 10.0.0.1", "--remote-debugging-pipe", "--window-size=1280,800",
    ];

    private static readonly string[] _systemd =
    [
        "systemd-run", "--scope", "--quiet", "--collect", "-p", "MemoryMax=1024M", "-p", "MemorySwapMax=0", "-p", "TasksMax=512", "--",
    ];

    [Fact]
    public void Headless_launch_with_systemd_is_exact()
    {
        string[] expected = [.. _systemd, .. _sandbox, .. _mounts, .. _chromiumCommon, "--headless=new", "--disable-gpu", "--", "about:blank"];

        Assert.Equal(expected, Build().Launch);
    }

    [Fact]
    public void Headless_launch_without_systemd_run_starts_at_setpriv()
    {
        var o = new BrowserPoolOptions { UseSystemdRun = false, NetworkIsolation = NetworkIsolation.SharedWithUidRule };
        string[] expected = [.. _sandbox, .. _mounts, .. _chromiumCommon, "--headless=new", "--disable-gpu", "--", "about:blank"];

        Assert.Equal(expected, Build(o).Launch);
    }

    [Fact]
    public void Zero_memory_omits_the_memory_properties_but_keeps_the_task_limit()
    {
        var o = new BrowserPoolOptions { MemoryMaxMb = 0 };

        Assert.Equal(
            ["systemd-run", "--scope", "--quiet", "--collect", "-p", "TasksMax=512", "--"],
            Build(o).Launch.Take(7));
    }

    [Fact]
    public void Memory_value_is_rendered_in_megabytes()
    {
        var o = new BrowserPoolOptions { MemoryMaxMb = 2048 };

        Assert.Contains("MemoryMax=2048M", Build(o).Launch);
    }

    [Fact]
    public void Headed_launch_adds_cage_wayland_env_and_drops_headless()
    {
        var o = new BrowserPoolOptions { UseSystemdRun = false, NetworkIsolation = NetworkIsolation.SharedWithUidRule };
        var launch = Build(o, r: new PoolSessionRequest("agent-1", PoolDisplayMode.Headed)).Launch;

        Assert.Contains("--ozone-platform=wayland", launch);
        Assert.DoesNotContain("--headless=new", launch);
        var cage = launch.ToList().IndexOf("/usr/bin/cage");
        Assert.True(cage > 0);
        Assert.Equal("--", launch[cage + 1]);
        Assert.Equal("/usr/bin/chromium", launch[cage + 2]);
        var text = string.Join('\u0001', launch);
        Assert.Contains("--setenv\u0001WLR_BACKENDS\u0001headless", text);
        Assert.Contains("--setenv\u0001WLR_RENDERER\u0001pixman", text);
        Assert.Contains("--setenv\u0001WLR_LIBINPUT_NO_DEVICES\u0001" + "1", text);
    }

    [Fact]
    public void Headless_launch_has_no_compositor_and_no_wayland_env()
    {
        var launch = Build().Launch;

        Assert.DoesNotContain("/usr/bin/cage", launch);
        Assert.DoesNotContain("WLR_BACKENDS", launch);
        Assert.DoesNotContain("--ozone-platform=wayland", launch);
    }

    [Fact]
    public void Prepare_commands_are_exact()
    {
        var spec = Build();

        Assert.Equal(
            [
                ["rm", "-rf", "--one-file-system", "/var/lib/bazaar-pool/slot-3"],
                ["install", "-d", "-m", "0700", "-o", "20003", "-g", "20003", "/var/lib/bazaar-pool/slot-3"],
                ["install", "-d", "-m", "0700", "-o", "20003", "-g", "20003", "/var/lib/bazaar-pool/slot-3/profile"],
                ["install", "-d", "-m", "0700", "-o", "20003", "-g", "20003", "/var/lib/bazaar-pool/slot-3/run"],
            ],
            spec.Prepare.Select(c => c.Argv));
        Assert.All(spec.Prepare, c => Assert.Null(c.AllowedExitCodes));
    }

    [Fact]
    public void Cleanup_commands_are_exact_and_pkill_tolerates_no_match()
    {
        var spec = Build();

        Assert.Equal(["pkill", "-KILL", "-U", "20003"], spec.Cleanup[0].Argv);
        Assert.Equal([0, 1], spec.Cleanup[0].AllowedExitCodes);
        Assert.Equal(["rm", "-rf", "--one-file-system", "/var/lib/bazaar-pool/slot-3"], spec.Cleanup[1].Argv);
        Assert.Null(spec.Cleanup[1].AllowedExitCodes);
        Assert.Equal(2, spec.Cleanup.Count);
    }

    [Fact]
    public void Spec_carries_identity_and_firewall_rule()
    {
        var spec = Build();

        Assert.Equal("sess1", spec.SessionId);
        Assert.Equal("agent-1", spec.AgentId);
        Assert.Equal(3, spec.Slot);
        Assert.Equal(20003, spec.Uid);
        Assert.Equal(new PoolFirewallRule(20003, "10.0.0.1", 3128), spec.Firewall);
    }

    [Theory]
    [InlineData(0, 20000)]
    [InlineData(1, 20001)]
    [InlineData(31, 20031)]
    public void Slot_uid_is_base_plus_slot(int slot, int uid) => Assert.Equal(uid, PoolCommandLine.SlotUid(new BrowserPoolOptions(), slot));

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    [InlineData(1000)]
    public void Slot_outside_the_pool_is_refused(int slot)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PoolCommandLine.SlotUid(new BrowserPoolOptions(), slot));
        Assert.Throws<ArgumentOutOfRangeException>(() => PoolCommandLine.SlotDirectory(new BrowserPoolOptions(), slot));
    }

    [Fact]
    public void Slot_directory_is_built_from_root_and_integer()
    {
        var o = new BrowserPoolOptions { SlotRoot = "/srv2/pool.d" };

        Assert.Equal("/srv2/pool.d/slot-7", PoolCommandLine.SlotDirectory(o, 7));
    }

    [Fact]
    public void Url_is_normalised_and_comes_last_after_double_dash()
    {
        var launch = Build(r: new PoolSessionRequest("agent-1", StartUrl: "HTTP://Example.COM/a b")).Launch;

        Assert.Equal("--", launch[^2]);
        Assert.Equal("http://example.com/a%20b", launch[^1]);
    }

    [Fact]
    public void Window_size_follows_the_request()
    {
        var launch = Build(r: new PoolSessionRequest("agent-1", Width: 1920, Height: 1080)).Launch;

        Assert.Contains("--window-size=1920,1080", launch);
    }

    [Fact]
    public void Sandbox_prefix_ends_before_the_program_and_has_the_privilege_drop()
    {
        var prefix = PoolCommandLine.SandboxPrefix(new BrowserPoolOptions(), 3, false);

        string[] expected = [.. _sandbox, .. _mounts];
        Assert.Equal(expected, prefix);
    }

    [Fact]
    public void Sandbox_never_binds_host_secrets_or_sockets_and_does_not_unshare_net()
    {
        var prefix = PoolCommandLine.SandboxPrefix(new BrowserPoolOptions(), 3, true);
        var binds = new List<string>();
        for (var i = 0; i < prefix.Count; i++)
        {
            if (prefix[i] is "--bind" or "--ro-bind" or "--ro-bind-try")
            {
                binds.Add(prefix[i + 1]);
            }
        }

        Assert.Equal(
            ["/usr", "/etc/fonts", "/etc/ssl", "/etc/ca-certificates", "/var/lib/bazaar-pool/slot-3/profile", "/var/lib/bazaar-pool/slot-3/run"],
            binds);
        Assert.DoesNotContain("--unshare-net", prefix);
        Assert.DoesNotContain("--share-net", prefix);
        Assert.DoesNotContain("--cap-add", prefix);
    }

    [Fact]
    public void Extra_binds_are_read_only_and_at_the_same_path()
    {
        var o = new BrowserPoolOptions { ExtraReadOnlyBinds = ["/opt/chromium-tree"] };
        var prefix = PoolCommandLine.SandboxPrefix(o, 3, false).ToList();

        var i = prefix.IndexOf("/opt/chromium-tree");
        Assert.Equal("--ro-bind", prefix[i - 1]);
        Assert.Equal("/opt/chromium-tree", prefix[i + 1]);
    }

    [Fact]
    public void Build_is_deterministic()
    {
        Assert.Equal(Build().Launch, Build().Launch);
    }

    [Fact]
    public void Different_slots_get_different_uids_and_directories()
    {
        var a = Build(slot: 1);
        var b = Build(slot: 2);

        Assert.NotEqual(a.Uid, b.Uid);
        Assert.DoesNotContain(a.Launch, x => x.Contains("slot-2", StringComparison.Ordinal));
        Assert.DoesNotContain(b.Launch, x => x.Contains("slot-1", StringComparison.Ordinal));
    }

    private static readonly string[] _forbiddenFlags =
    [
        "--no-sandbox", "--disable-web-security", "--remote-debugging-port", "--allow-running-insecure-content", "--disable-site-isolation-trials",
        "--disable-setuid-sandbox", "--disable-gpu-sandbox", "--single-process", "--remote-debugging-address", "--disable-namespace-sandbox",
        "--incognito",
    ];

    public static TheoryData<bool, bool, int, string?, bool> Combinations()
    {
        var data = new TheoryData<bool, bool, int, string?, bool>();
        foreach (var headed in new[] { false, true })
        {
            foreach (var systemd in new[] { false, true })
            {
                foreach (var mem in new[] { 0, 512 })
                {
                    foreach (var url in new string?[] { null, "https://example.org/x?y=1" })
                    {
                        foreach (var extra in new[] { false, true })
                        {
                            data.Add(headed, systemd, mem, url, extra);
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void No_option_combination_produces_a_forbidden_flag(bool headed, bool systemd, int mem, string? url, bool extra)
    {
        var o = new BrowserPoolOptions { UseSystemdRun = systemd, MemoryMaxMb = mem, ExtraReadOnlyBinds = extra ? ["/opt/x"] : [] };
        var launch = Build(o, r: new PoolSessionRequest("agent-1", headed ? PoolDisplayMode.Headed : PoolDisplayMode.Headless, url)).Launch;

        foreach (var arg in launch)
        {
            foreach (var bad in _forbiddenFlags)
            {
                Assert.False(arg.StartsWith(bad, StringComparison.Ordinal), $"forbidden flag {bad} in {arg}");
            }
        }

        Assert.DoesNotContain("--cap-add", launch);
    }

    [Theory]
    [InlineData("/usr/bin/chromium --no-sandbox")]
    [InlineData("-x")]
    [InlineData("chromium")]
    [InlineData("/usr/bin/../bin/chromium")]
    [InlineData("/usr/bin/chromium;id")]
    [InlineData("/usr/bin/chromium\n--no-sandbox")]
    [InlineData("")]
    public void Chromium_path_injection_is_rejected_by_validation_and_by_build(string path)
    {
        var o = new BrowserPoolOptions { ChromiumPath = path };

        Assert.NotEmpty(BrowserPoolOptionsValidator.Check(o));
        Assert.Throws<ArgumentException>(() => Build(o));
    }

    [Theory]
    [InlineData("10.0.0.1 --no-sandbox")]
    [InlineData("-evil")]
    [InlineData("proxy.internal")]
    [InlineData("10.0.0.1,example.com")]
    [InlineData("::1")]
    [InlineData("10.1")]
    [InlineData("")]
    public void Proxy_host_injection_is_rejected(string host)
    {
        Assert.Throws<ArgumentException>(() => Build(e: new PoolEgressEndpoint(host, 3128)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Proxy_port_out_of_range_is_rejected(int port)
    {
        Assert.Throws<ArgumentException>(() => Build(e: new PoolEgressEndpoint("10.0.0.1", port)));
    }

    [Fact]
    public void Invalid_request_is_rejected_by_build()
    {
        Assert.Throws<ArgumentException>(() => Build(r: new PoolSessionRequest("bad agent")));
        Assert.Throws<ArgumentException>(() => Build(r: new PoolSessionRequest("a", StartUrl: "--no-sandbox")));
    }

    [Fact]
    public void Invalid_options_object_is_rejected_by_build_even_if_not_validated_at_startup()
    {
        Assert.Throws<ArgumentException>(() => Build(new BrowserPoolOptions { SlotRoot = "/var" }));
    }
}
