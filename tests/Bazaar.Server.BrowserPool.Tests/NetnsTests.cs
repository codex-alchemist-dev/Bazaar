using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Tests;

/// <summary>Per-slot network namespace: option validation, every rendered argv and nft line, and the service's ordering and failure paths.</summary>
public class NetnsTests
{
    private static readonly PoolEgressEndpoint _loopbackEgress = new("127.0.0.1", 3128);
    private static readonly PoolEgressEndpoint _remoteEgress = new("10.0.0.1", 3128);

    private static BrowserPoolOptions PerSlot() => new() { NetworkIsolation = NetworkIsolation.PerSlotNamespace };

    private static PoolLaunchSpec Build(BrowserPoolOptions? o = null, int slot = 3, PoolEgressEndpoint? e = null) =>
        PoolCommandLine.Build(o ?? PerSlot(), "sess1", slot, new PoolSessionRequest("agent-1"), e ?? _remoteEgress);

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    // ---- options ----

    [Fact]
    public void Per_slot_namespace_is_the_default_and_the_default_options_are_valid()
    {
        var o = new BrowserPoolOptions();

        Assert.Equal(NetworkIsolation.PerSlotNamespace, o.NetworkIsolation);
        Assert.Empty(BrowserPoolOptionsValidator.Check(o));
    }

    [Theory]
    [InlineData("10.222.0.0", 32, true)]
    [InlineData("172.16.0.0", 1000, true)]
    [InlineData("192.168.0.0", 1000, true)]
    [InlineData("169.254.0.0", 1000, true)]
    [InlineData("10.255.255.0", 65, false)] // runs off the end of 10.0.0.0/8
    [InlineData("10.255.255.0", 64, true)]
    [InlineData("10.255.255.128", 33, false)]
    [InlineData("192.168.255.0", 100, false)] // would leave 192.168.0.0/16
    [InlineData("172.15.255.0", 32, false)] // just below 172.16.0.0/12
    [InlineData("172.32.0.0", 32, false)] // just above it
    [InlineData("10.222.0.1", 32, false)] // not a multiple of 4
    [InlineData("10.222.0.2", 32, false)]
    [InlineData("8.8.8.0", 32, false)] // public
    [InlineData("127.0.0.0", 32, false)] // loopback
    [InlineData("100.64.0.0", 32, false)] // shared address space is not in the allowed blocks
    [InlineData("0.0.0.0", 32, false)]
    public void Base_address_must_hold_one_slash_30_per_slot_inside_a_private_block(string baseAddress, int maxSessions, bool ok)
    {
        var o = PerSlot();
        o.NetnsBaseAddress = baseAddress;
        o.MaxSessions = maxSessions;
        o.MaxSessionsPerAgent = 1;
        o.SlotUidBase = 20000;

        Assert.Equal(ok, BrowserPoolOptionsValidator.Check(o).Count == 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("10.222.0.0; rm -rf /")]
    [InlineData("10.222.0.0\nflush ruleset")]
    [InlineData("10.222.0.0 ")]
    [InlineData("10.222.0.0/16")]
    [InlineData("10.222")]
    [InlineData("010.222.0.0")]
    [InlineData("::1")]
    [InlineData("0xA.222.0.0")]
    public void Base_address_that_is_not_a_canonical_ipv4_literal_is_refused(string baseAddress)
    {
        var o = PerSlot();
        o.NetnsBaseAddress = baseAddress;

        Assert.NotEmpty(BrowserPoolOptionsValidator.Check(o));
        Assert.Throws<ArgumentException>(() => PoolCommandLine.SlotUid(o, 0));
    }

    [Fact]
    public void Base_address_is_not_checked_in_shared_mode()
    {
        var o = new BrowserPoolOptions { NetworkIsolation = NetworkIsolation.SharedWithUidRule, NetnsBaseAddress = "not an address" };

        Assert.Empty(BrowserPoolOptionsValidator.Check(o));
    }

    [Theory]
    [InlineData("IpPath")]
    [InlineData("NsenterPath")]
    [InlineData("SysctlPath")]
    public void Tool_paths_must_be_safe_absolute_paths(string which)
    {
        foreach (var bad in new[] { "ip", "/usr/sbin/ip; id", "/usr/sbin/../bin/ip", "/usr/sbin/ip --", "/usr/sbin/ ip", "" })
        {
            var o = PerSlot();
            switch (which)
            {
                case "IpPath": o.IpPath = bad; break;
                case "NsenterPath": o.NsenterPath = bad; break;
                default: o.SysctlPath = bad; break;
            }

            Assert.NotEmpty(BrowserPoolOptionsValidator.Check(o));
        }
    }

    [Fact]
    public void Unknown_isolation_value_is_refused()
    {
        var o = new BrowserPoolOptions { NetworkIsolation = (NetworkIsolation)7 };

        Assert.NotEmpty(BrowserPoolOptionsValidator.Check(o));
    }

    [Theory]
    [InlineData("eth0")]
    [InlineData("ens3")]
    [InlineData("br-pool.1")]
    [InlineData("")]
    public void Egress_interface_that_is_a_plain_name_is_accepted(string name)
    {
        var o = PerSlot();
        o.NetnsEgressInterface = name;

        Assert.Empty(BrowserPoolOptionsValidator.Check(o));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("default")]
    [InlineData("lo")]
    [InlineData("bzv3")]
    [InlineData("bzp3")]
    [InlineData("eth0.forwarding=1 ")]
    [InlineData("eth0/../all")]
    [InlineData("eth0;id")]
    [InlineData("eth 0")]
    [InlineData("0eth")]
    [InlineData("averyveryverylongname")]
    [InlineData("eth0\n")]
    public void Egress_interface_that_could_widen_the_sysctl_is_refused(string name)
    {
        var o = PerSlot();
        o.NetnsEgressInterface = name;

        Assert.NotEmpty(BrowserPoolOptionsValidator.Check(o));
        Assert.Throws<ArgumentException>(() => PoolNetwork.SetupCommands(o, 0, _remoteEgress));
    }

    [Fact]
    public void Egress_interface_adds_one_scoped_forwarding_command_for_a_remote_endpoint_only()
    {
        var o = PerSlot();
        o.NetnsEgressInterface = "eth0";

        var remote = PoolNetwork.SetupCommands(o, 3, _remoteEgress);
        var loopback = PoolNetwork.SetupCommands(o, 3, _loopbackEgress);

        Assert.Equal(["/usr/sbin/sysctl", "-w", "net.ipv4.conf.eth0.forwarding=1"], remote[^1].Argv);
        Assert.Equal(["/usr/sbin/sysctl", "-w", "net.ipv4.conf.bzv3.forwarding=1"], remote[^2].Argv);
        Assert.Equal(["/usr/sbin/sysctl", "-w", "net.ipv4.conf.bzv3.route_localnet=1"], loopback[^1].Argv);
        Assert.Equal(remote.Count - 1, PoolNetwork.SetupCommands(PerSlot(), 3, _remoteEgress).Count);
    }

    // ---- addresses ----

    [Fact]
    public void Addresses_are_derived_from_the_slot_only_and_never_overlap()
    {
        var o = PerSlot();
        o.MaxSessions = 1000;
        o.MaxSessionsPerAgent = 4;
        o.NetnsBaseAddress = "10.0.0.0";
        var seen = new HashSet<string>();
        for (var slot = 0; slot < 1000; slot++)
        {
            Assert.True(seen.Add(PoolNetwork.HostAddress(o, slot)));
            Assert.True(seen.Add(PoolNetwork.SlotAddress(o, slot)));
        }

        Assert.Equal("10.0.0.1", PoolNetwork.HostAddress(o, 0));
        Assert.Equal("10.0.0.2", PoolNetwork.SlotAddress(o, 0));
        Assert.Equal("10.0.0.5", PoolNetwork.HostAddress(o, 1));
        Assert.Equal("10.0.15.157", PoolNetwork.HostAddress(o, 999));
        Assert.Equal("10.0.15.158", PoolNetwork.SlotAddress(o, 999));
    }

    [Fact]
    public void Names_are_a_fixed_prefix_and_an_integer()
    {
        Assert.Equal("bzpool-7", PoolNetwork.NamespaceName(7));
        Assert.Equal("bzv7", PoolNetwork.HostInterface(7));
        Assert.Equal("bzp7", PoolNetwork.SlotInterface(7));
        Assert.Throws<ArgumentOutOfRangeException>(() => PoolNetwork.NamespaceName(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PoolNetwork.HostInterface(1001));
        Assert.True(PoolNetwork.HostInterface(1000).Length <= 15, "an interface name may be 15 characters");
    }

    [Fact]
    public void Slot_outside_the_pool_is_refused()
    {
        var o = PerSlot();

        Assert.Throws<ArgumentOutOfRangeException>(() => PoolNetwork.SetupCommands(o, o.MaxSessions, _remoteEgress));
        Assert.Throws<ArgumentOutOfRangeException>(() => PoolNetwork.CleanupCommands(o, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PoolNetwork.HostAddress(o, o.MaxSessions));
    }

    // ---- commands ----

    [Fact]
    public void Setup_commands_for_a_remote_endpoint_are_exact_and_scope_forwarding_to_the_veth()
    {
        var commands = PoolNetwork.SetupCommands(PerSlot(), 3, _remoteEgress);

        Assert.Equal(
            [
                ["/usr/sbin/ip", "link", "del", "bzv3"],
                ["/usr/sbin/ip", "netns", "del", "bzpool-3"],
                ["/usr/sbin/ip", "netns", "add", "bzpool-3"],
                ["/usr/sbin/ip", "link", "add", "bzv3", "type", "veth", "peer", "name", "bzp3", "netns", "bzpool-3"],
                ["/usr/sbin/ip", "addr", "add", "10.222.0.13/30", "dev", "bzv3"],
                ["/usr/sbin/ip", "link", "set", "bzv3", "up"],
                ["/usr/sbin/ip", "-n", "bzpool-3", "addr", "add", "10.222.0.14/30", "dev", "bzp3"],
                ["/usr/sbin/ip", "-n", "bzpool-3", "link", "set", "bzp3", "up"],
                ["/usr/sbin/ip", "-n", "bzpool-3", "link", "set", "lo", "up"],
                ["/usr/sbin/sysctl", "-w", "net.ipv4.conf.bzv3.forwarding=1"],
            ],
            commands.Select(c => c.Argv));
        Assert.Equal([0, 1], commands[0].AllowedExitCodes);
        Assert.Equal([0, 1], commands[1].AllowedExitCodes);
        Assert.All(commands.Skip(2), c => Assert.Null(c.AllowedExitCodes));
    }

    [Fact]
    public void Setup_commands_for_a_loopback_endpoint_scope_route_localnet_to_the_veth()
    {
        var commands = PoolNetwork.SetupCommands(PerSlot(), 3, _loopbackEgress);

        Assert.Equal(["/usr/sbin/sysctl", "-w", "net.ipv4.conf.bzv3.route_localnet=1"], commands[^1].Argv);
        Assert.DoesNotContain(commands, c => c.Argv.Any(a => a.Contains(".all.", StringComparison.Ordinal) || a.Contains(".default.", StringComparison.Ordinal)));
        Assert.DoesNotContain(commands, c => c.Argv.Any(a => a.Contains("ip_forward", StringComparison.Ordinal)));
    }

    [Fact]
    public void Setup_adds_no_default_route_so_only_the_slash_30_is_reachable_from_inside()
    {
        var commands = PoolNetwork.SetupCommands(PerSlot(), 3, _remoteEgress);

        Assert.DoesNotContain(commands, c => c.Argv.Contains("route", StringComparer.Ordinal));
    }

    [Fact]
    public void Cleanup_commands_are_exact_idempotent_and_verify_the_result()
    {
        var commands = PoolNetwork.CleanupCommands(PerSlot(), 3);

        Assert.Equal(
            [
                ["/usr/sbin/ip", "link", "del", "bzv3"],
                ["/usr/sbin/ip", "netns", "del", "bzpool-3"],
                ["test", "!", "-e", "/run/netns/bzpool-3"],
                ["test", "!", "-e", "/sys/class/net/bzv3"],
            ],
            commands.Select(c => c.Argv));
        Assert.Equal([0, 1], commands[0].AllowedExitCodes);
        Assert.Equal([0, 1], commands[1].AllowedExitCodes);
        Assert.Null(commands[2].AllowedExitCodes);
        Assert.Null(commands[3].AllowedExitCodes);
    }

    [Fact]
    public void Slot_cleanup_kills_first_then_removes_the_network_then_wipes_the_directory()
    {
        var commands = PoolCommandLine.CleanupCommands(PerSlot(), 3);

        Assert.Equal("pkill", commands[0].Argv[0]);
        Assert.Equal("/usr/sbin/ip", commands[1].Argv[0]);
        Assert.Equal("rm", commands[^1].Argv[0]);
        Assert.Equal(6, commands.Count);
    }

    [Fact]
    public void Shared_mode_cleanup_is_unchanged()
    {
        var commands = PoolCommandLine.CleanupCommands(new BrowserPoolOptions { NetworkIsolation = NetworkIsolation.SharedWithUidRule }, 3);

        Assert.Equal(["pkill", "rm"], commands.Select(c => c.Argv[0]));
    }

    [Fact]
    public void Enter_prefix_is_nsenter_on_the_named_namespace_file()
    {
        Assert.Equal(["/usr/bin/nsenter", "--net=/run/netns/bzpool-3", "--"], PoolNetwork.EnterPrefix(PerSlot(), 3));
    }

    [Fact]
    public void Network_helpers_refuse_to_run_in_shared_mode()
    {
        var o = new BrowserPoolOptions { NetworkIsolation = NetworkIsolation.SharedWithUidRule };

        Assert.Throws<InvalidOperationException>(() => PoolNetwork.SetupCommands(o, 0, _remoteEgress));
        Assert.Throws<InvalidOperationException>(() => PoolNetwork.EnterPrefix(o, 0));
    }

    // ---- launch spec ----

    [Fact]
    public void Launch_enters_the_namespace_after_systemd_run_and_before_the_privilege_drop()
    {
        var launch = Build().Launch;

        var systemd = launch.ToList().IndexOf("systemd-run");
        var nsenter = launch.ToList().IndexOf("/usr/bin/nsenter");
        var setpriv = launch.ToList().IndexOf("/usr/bin/setpriv");
        var bwrap = launch.ToList().IndexOf("/usr/bin/bwrap");
        Assert.Equal(0, systemd);
        Assert.True(systemd < nsenter && nsenter < setpriv && setpriv < bwrap);
        Assert.Equal("--net=/run/netns/bzpool-3", launch[nsenter + 1]);
        Assert.Equal("--", launch[nsenter + 2]);
        Assert.Equal("/usr/bin/setpriv", launch[nsenter + 3]);
    }

    [Fact]
    public void Launch_without_systemd_starts_at_nsenter()
    {
        var o = PerSlot();
        o.UseSystemdRun = false;

        Assert.Equal("/usr/bin/nsenter", Build(o).Launch[0]);
    }

    [Fact]
    public void Headed_launch_enters_the_namespace_before_cage()
    {
        var spec = PoolCommandLine.Build(PerSlot(), "sess1", 3, new PoolSessionRequest("agent-1", PoolDisplayMode.Headed), _remoteEgress);
        var launch = spec.Launch.ToList();

        Assert.True(launch.IndexOf("/usr/bin/nsenter") < launch.IndexOf("/usr/bin/setpriv"));
        Assert.True(launch.IndexOf("/usr/bin/setpriv") < launch.IndexOf("/usr/bin/cage"));
    }

    [Fact]
    public void Browser_is_pointed_at_the_veth_address_never_the_real_endpoint_and_keeps_every_other_flag()
    {
        var spec = Build();

        Assert.Contains("--proxy-server=http://10.222.0.13:3128", spec.Launch);
        Assert.Contains("--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 10.222.0.13", spec.Launch);
        Assert.DoesNotContain(spec.Launch, a => a.Contains("10.0.0.1", StringComparison.Ordinal));
        Assert.DoesNotContain("--no-sandbox", spec.Launch);
        Assert.Contains("--bounding-set=-all", spec.Launch);
        Assert.Contains("--no-new-privs", spec.Launch);
        Assert.Contains("--unshare-pid", spec.Launch);
        Assert.DoesNotContain("--unshare-net", spec.Launch);
    }

    [Fact]
    public void Firewall_rule_still_names_the_real_endpoint()
    {
        Assert.Equal(new PoolFirewallRule(20003, "10.0.0.1", 3128), Build().Firewall);
    }

    [Fact]
    public void Prepare_creates_the_directory_then_the_network()
    {
        var spec = Build();

        Assert.Equal(["rm", "install", "install", "install"], spec.Prepare.Take(4).Select(c => c.Argv[0]));
        Assert.Equal(PoolNetwork.SetupCommands(PerSlot(), 3, _remoteEgress).Select(c => c.Argv), spec.Prepare.Skip(4).Select(c => c.Argv));
    }

    [Fact]
    public void Shared_mode_launch_has_no_namespace_prefix_and_points_at_the_real_endpoint()
    {
        var o = new BrowserPoolOptions { NetworkIsolation = NetworkIsolation.SharedWithUidRule };
        var spec = Build(o);

        Assert.DoesNotContain("/usr/bin/nsenter", spec.Launch);
        Assert.Contains("--proxy-server=http://10.0.0.1:3128", spec.Launch);
        Assert.Equal(4, spec.Prepare.Count);
    }

    [Theory]
    [InlineData("10.222.0.1")]
    [InlineData("10.222.0.13")]
    [InlineData("10.222.0.127")] // last address of slot 31's block
    public void Endpoint_inside_the_slot_range_is_refused(string host)
    {
        Assert.Throws<ArgumentException>(() => Build(e: new PoolEgressEndpoint(host, 3128)));
    }

    [Fact]
    public void Endpoint_just_outside_the_slot_range_is_allowed()
    {
        Assert.NotNull(Build(e: new PoolEgressEndpoint("10.222.0.128", 3128)));
        Assert.NotNull(Build(e: new PoolEgressEndpoint("10.221.255.255", 3128)));
    }

    [Theory]
    [InlineData("10.0.0.1 ; id")]
    [InlineData("10.0.0.1\n")]
    [InlineData("proxy.example")]
    [InlineData("10.0.0.1,accept")]
    public void Endpoint_text_that_is_not_an_ipv4_literal_never_reaches_a_command(string host)
    {
        Assert.Throws<ArgumentException>(() => Build(e: new PoolEgressEndpoint(host, 3128)));
        Assert.Throws<ArgumentException>(() => PoolNetwork.SetupCommands(PerSlot(), 3, new PoolEgressEndpoint(host, 3128)));
    }

    [Fact]
    public void No_netns_argv_element_carries_a_shell_metacharacter_or_whitespace()
    {
        var spec = Build();
        var all = spec.Prepare.Concat(spec.Cleanup).SelectMany(c => c.Argv)
            .Concat(PoolNetwork.EnterPrefix(PerSlot(), 3));

        Assert.All(all, a => Assert.DoesNotContain(a, c => char.IsWhiteSpace(c) || ";|&$`<>(){}\\\"'*?".Contains(c)));
    }

    // ---- nftables ----

    [Fact]
    public void Nft_document_for_one_loopback_slot_is_exact()
    {
        var text = NftablesRules.Render([new PoolFirewallRule(20000, "127.0.0.1", 3128)], PerSlot());

        Assert.Equal(
            Lines(
                "table inet bazaar_pool",
                "delete table inet bazaar_pool",
                "table inet bazaar_pool {",
                "  chain output {",
                "    type filter hook output priority 0; policy accept;",
                "    meta skuid 20000 ip daddr 127.0.0.1 tcp dport 3128 accept",
                "    meta skuid >= 20000 meta skuid <= 20031 drop",
                "  }",
                "  chain prerouting {",
                "    type nat hook prerouting priority dstnat; policy accept;",
                "    iifname \"bzv0\" ip daddr 10.222.0.1 tcp dport 3128 dnat ip to 127.0.0.1:3128",
                "  }",
                "  chain postrouting {",
                "    type nat hook postrouting priority srcnat; policy accept;",
                "  }",
                "  chain input {",
                "    type filter hook input priority 0; policy accept;",
                "    iifname \"bzv0\" ip daddr 127.0.0.1 tcp dport 3128 ct status dnat accept",
                "    iifname \"bzv*\" drop",
                "  }",
                "  chain forward {",
                "    type filter hook forward priority 0; policy accept;",
                "    iifname \"bzv0\" ip daddr 127.0.0.1 tcp dport 3128 ct status dnat accept",
                "    iifname \"bzv*\" drop",
                "  }",
                "  chain forward_in {",
                "    type filter hook forward priority 1; policy accept;",
                "    oifname \"bzv*\" ct state new drop",
                "  }",
                "}"),
            text);
    }

    [Fact]
    public void Nft_document_for_a_remote_endpoint_masquerades_only_that_flow()
    {
        var text = NftablesRules.Render([new PoolFirewallRule(20003, "10.0.0.1", 3128)], PerSlot());

        Assert.Contains("    iifname \"bzv3\" ip daddr 10.222.0.13 tcp dport 3128 dnat ip to 10.0.0.1:3128\n", text, StringComparison.Ordinal);
        Assert.Contains("    ip saddr 10.222.0.14 ip daddr 10.0.0.1 tcp dport 3128 masquerade\n", text, StringComparison.Ordinal);
        Assert.Equal(1, text.Split("masquerade").Length - 1);
    }

    [Fact]
    public void Nft_document_renders_every_active_slot_sorted_and_still_drops_stale_veths()
    {
        var text = NftablesRules.Render(
            [new PoolFirewallRule(20002, "10.0.0.2", 3129), new PoolFirewallRule(20000, "10.0.0.1", 3128)],
            PerSlot());

        Assert.True(text.IndexOf("iifname \"bzv0\" ip daddr 10.222.0.1 ", StringComparison.Ordinal) < text.IndexOf("iifname \"bzv2\" ip daddr 10.222.0.9 ", StringComparison.Ordinal));
        Assert.Contains("iifname \"bzv2\" ip daddr 10.222.0.9 tcp dport 3129 dnat ip to 10.0.0.2:3129", text, StringComparison.Ordinal);

        var empty = NftablesRules.Render([], PerSlot());
        Assert.Contains("    iifname \"bzv*\" drop\n", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("dnat", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("masquerade", empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Nft_document_drop_follows_every_accept_in_each_veth_chain()
    {
        var text = NftablesRules.Render([new PoolFirewallRule(20000, "10.0.0.1", 3128), new PoolFirewallRule(20001, "10.0.0.1", 3128)], PerSlot());

        foreach (var chain in new[] { "chain input {", "chain forward {" })
        {
            var section = text[text.IndexOf(chain, StringComparison.Ordinal)..];
            section = section[..section.IndexOf("  }\n", StringComparison.Ordinal)];
            Assert.True(section.LastIndexOf("accept\n", StringComparison.Ordinal) < section.IndexOf("drop\n", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Nft_document_touches_only_its_own_table()
    {
        var text = NftablesRules.Render([new PoolFirewallRule(20000, "10.0.0.1", 3128)], PerSlot());

        Assert.DoesNotContain("flush", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ruleset", text, StringComparison.Ordinal);
        var tableLines = text.Split('\n').Where(l => l.Contains("table ", StringComparison.Ordinal)).ToList();
        Assert.All(tableLines, l => Assert.Contains("inet bazaar_pool", l, StringComparison.Ordinal));
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("table inet bazaar_pool", StringComparison.Ordinal)));
        Assert.Single(text.Split('\n'), l => l.StartsWith("delete ", StringComparison.Ordinal));
    }

    [Fact]
    public void Nft_document_in_shared_mode_has_no_veth_chains_and_equals_the_uid_only_document()
    {
        var o = new BrowserPoolOptions { NetworkIsolation = NetworkIsolation.SharedWithUidRule };
        var rules = new[] { new PoolFirewallRule(20000, "10.0.0.1", 3128) };

        var text = NftablesRules.Render(rules, o);

        Assert.DoesNotContain("bzv", text, StringComparison.Ordinal);
        Assert.Equal(NftablesRules.Render(rules, o.SlotUidBase, o.MaxUid), text);
    }

    [Theory]
    [InlineData("10.0.0.1\n flush ruleset")]
    [InlineData("10.0.0.1; drop")]
    [InlineData("10.0.0.1 accept")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void Nft_document_refuses_a_host_that_is_not_an_ipv4_literal(string host)
    {
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([new PoolFirewallRule(20000, host, 3128)], PerSlot()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void Nft_document_refuses_a_port_out_of_range(int port)
    {
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([new PoolFirewallRule(20000, "10.0.0.1", port)], PerSlot()));
    }

    [Fact]
    public void Nft_document_refuses_a_uid_outside_the_slot_range_and_invalid_options()
    {
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([new PoolFirewallRule(20032, "10.0.0.1", 3128)], PerSlot()));
        Assert.Throws<ArgumentException>(() => NftablesRules.Render([], new BrowserPoolOptions { NetnsBaseAddress = "x" }));
    }

    // ---- service ordering and failure paths ----

    private static Rig PerSlotRig(Action<BrowserPoolOptions>? configure = null) =>
        new(o =>
        {
            o.NetworkIsolation = NetworkIsolation.PerSlotNamespace;
            configure?.Invoke(o);
        });

    [Fact]
    public async Task Open_runs_directory_prepare_then_netns_setup_then_firewall_then_start()
    {
        using var rig = PerSlotRig();

        await rig.OpenAsync("agent-1");

        Assert.Equal(
            [
                "run:rm", "run:install", "run:install", "run:install",
                "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:/usr/sbin/ip",
                "run:/usr/sbin/sysctl",
                "fw", "start",
            ],
            rig.Host.Log);
        Assert.Equal("/usr/bin/nsenter", rig.Host.Started[0].Launch.First(a => a.StartsWith("/usr/bin/n", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Close_kills_then_removes_the_network_then_wipes_the_directory()
    {
        using var rig = PerSlotRig();
        var s = await rig.OpenAsync("agent-1");
        var before = rig.Host.Log.Count;

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        var tail = rig.Host.Log.Skip(before).Where(l => l != "fw").ToList();
        Assert.Equal(["stop", "run:pkill", "run:/usr/sbin/ip", "run:/usr/sbin/ip", "run:test", "run:test", "run:rm"], tail);
        Assert.Contains(rig.Host.Ran, c => c.Argv.SequenceEqual(["/usr/sbin/ip", "netns", "del", "bzpool-0"]));
    }

    [Theory]
    [InlineData("netns")] // ip netns add
    [InlineData("veth")] // ip link add ... veth
    [InlineData("addr")]
    [InlineData("sysctl")]
    public async Task Failed_network_setup_fails_the_open_before_the_firewall_and_the_launch_and_still_cleans_up(string failing)
    {
        using var rig = PerSlotRig();
        rig.Host.FailRun = c => failing switch
        {
            "netns" => c.Argv.SequenceEqual(["/usr/sbin/ip", "netns", "add", "bzpool-0"]),
            "veth" => c.Argv.Contains("veth"),
            "addr" => c.Argv.Contains("addr") && c.Argv.Contains("-n"),
            _ => c.Argv[0] == "/usr/sbin/sysctl",
        };

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => rig.OpenAsync("agent-1"));

        Assert.Equal(PoolFailure.LaunchFailed, ex.Failure);
        Assert.Empty(rig.Host.Started);
        Assert.DoesNotContain("start", rig.Host.Log);
        Assert.DoesNotContain("fw", rig.Host.Log.Take(rig.Host.Log.IndexOf("run:pkill")));
        Assert.Equal(1, rig.Host.Count("pkill"));
        Assert.Contains(rig.Host.Ran, c => c.Argv.SequenceEqual(["/usr/sbin/ip", "netns", "del", "bzpool-0"]) && rig.Host.Ran.IndexOf(c) > rig.Host.Ran.FindIndex(x => x.Argv[0] == "pkill"));
        Assert.Empty(rig.Pool.Snapshot().Sessions);
        Assert.Empty(rig.Pool.Snapshot().Quarantined);
    }

    [Fact]
    public async Task Failed_firewall_fails_the_open_and_removes_the_namespace_that_was_already_made()
    {
        using var rig = PerSlotRig();
        rig.Host.FailFirewall = true;

        var ex = await Assert.ThrowsAsync<BrowserPoolException>(() => rig.OpenAsync("agent-1"));

        Assert.Equal(PoolFailure.LaunchFailed, ex.Failure);
        Assert.Empty(rig.Host.Started);
        var pkill = rig.Host.Ran.FindIndex(c => c.Argv[0] == "pkill");
        Assert.True(rig.Host.Ran.FindIndex(pkill, c => c.Argv.SequenceEqual(["/usr/sbin/ip", "netns", "del", "bzpool-0"])) > pkill);
    }

    [Fact]
    public async Task Failed_start_removes_the_namespace()
    {
        using var rig = PerSlotRig();
        rig.Host.OnStart = (_, _) => throw new InvalidOperationException("no chromium");

        await Assert.ThrowsAsync<BrowserPoolException>(() => rig.OpenAsync("agent-1"));

        var pkill = rig.Host.Ran.FindIndex(c => c.Argv[0] == "pkill");
        Assert.True(rig.Host.Ran.FindIndex(pkill, c => c.Argv.SequenceEqual(["/usr/sbin/ip", "link", "del", "bzv0"])) > pkill);
        Assert.Contains(rig.Host.Ran.Skip(pkill), c => c.Argv.SequenceEqual(["/usr/sbin/ip", "netns", "del", "bzpool-0"]));
    }

    [Fact]
    public async Task A_namespace_that_is_still_there_after_cleanup_quarantines_the_slot()
    {
        using var rig = PerSlotRig(o => { o.MaxSessions = 2; o.MaxSessionsPerAgent = 2; });
        var s = await rig.OpenAsync("agent-1");
        rig.Host.FailRun = c => c.Argv[0] == "test";

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(0, Assert.Single(rig.Pool.Snapshot().Quarantined).Slot);
        Assert.Single(rig.Audit.QuarantineRows);
        Assert.Equal(1, (await rig.OpenAsync("agent-2")).Slot);
    }

    [Fact]
    public async Task Cleanup_keeps_going_when_the_delete_fails_so_the_directory_is_still_wiped()
    {
        using var rig = PerSlotRig();
        var s = await rig.OpenAsync("agent-1");
        var rmBefore = rig.Host.Count("rm");
        rig.Host.FailRun = c => c.Argv.SequenceEqual(["/usr/sbin/ip", "netns", "del", "bzpool-0"]);

        await rig.Pool.CloseAsync(s.SessionId, TestContext.Current.CancellationToken);

        Assert.Equal(rmBefore + 1, rig.Host.Count("rm"));
        Assert.Single(rig.Pool.Snapshot().Quarantined);
    }

    [Fact]
    public async Task Firewall_rules_in_per_slot_mode_carry_the_real_endpoint_and_the_document_renders()
    {
        using var rig = PerSlotRig();
        rig.Egress.Resolver = _ => Task.FromResult<PoolEgressEndpoint?>(_loopbackEgress);

        await rig.OpenAsync("agent-1");

        var rules = rig.Host.Firewalls[0];
        Assert.Equal([new PoolFirewallRule(20000, "127.0.0.1", 3128)], rules);
        Assert.Contains("dnat ip to 127.0.0.1:3128", NftablesRules.Render(rules, rig.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_endpoint_inside_the_slot_range_refuses_the_open_without_touching_the_network()
    {
        using var rig = PerSlotRig();
        rig.Egress.Resolver = _ => Task.FromResult<PoolEgressEndpoint?>(new PoolEgressEndpoint("10.222.0.5", 3128));

        await Assert.ThrowsAnyAsync<Exception>(() => rig.OpenAsync("agent-1"));

        Assert.Empty(rig.Host.Started);
        Assert.DoesNotContain(rig.Host.Ran, c => c.Argv.Contains("add"));
    }
}
