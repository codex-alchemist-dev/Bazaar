using System.Net;
using Bazaar.Server.VmEgress.Rules;

namespace Bazaar.Server.VmEgress.Tests;

public class RuleGeneratorTests
{
    private static EgressRules Rules(string address, int port) => new(["example.com"], IPAddress.Parse(address), port);

    [Fact]
    public void HyperV_rules_allow_only_the_proxy_and_deny_the_rest_both_ways_and_both_families()
    {
        var acls = HyperVAclRules.Generate(Rules("10.99.0.1", 3128));

        Assert.Equal(
            [
                new HyperVAclRule("Allow", "Outbound", "10.99.0.1", "TCP", 3128, 100, true),
                new HyperVAclRule("Allow", "Inbound", "10.99.0.1", "TCP", 3128, 100, true),
                new HyperVAclRule("Deny", "Outbound", "0.0.0.0/0", null, null, 1, false),
                new HyperVAclRule("Deny", "Inbound", "0.0.0.0/0", null, null, 1, false),
                new HyperVAclRule("Deny", "Outbound", "::/0", null, null, 1, false),
                new HyperVAclRule("Deny", "Inbound", "::/0", null, null, 1, false),
            ],
            acls);
        Assert.All(acls.Where(a => a.Action == "Allow"), a => Assert.True(a.Weight > acls.Where(d => d.Action == "Deny").Max(d => d.Weight)));
    }

    [Fact]
    public void HyperV_powershell_golden()
    {
        var script = HyperVAclRules.ToPowerShell("agent-1-linux", HyperVAclRules.Generate(Rules("10.99.0.1", 3128)));

        const string expected =
            "Get-VMNetworkAdapterExtendedAcl -VMName 'agent-1-linux' | Remove-VMNetworkAdapterExtendedAcl\n"
            + "Add-VMNetworkAdapterExtendedAcl -VMName 'agent-1-linux' -Action Allow -Direction Outbound -RemoteIPAddress '10.99.0.1' -Protocol TCP -RemotePort 3128 -Weight 100 -Stateful $true\n"
            + "Add-VMNetworkAdapterExtendedAcl -VMName 'agent-1-linux' -Action Allow -Direction Inbound -RemoteIPAddress '10.99.0.1' -Protocol TCP -RemotePort 3128 -Weight 100 -Stateful $true\n"
            + "Add-VMNetworkAdapterExtendedAcl -VMName 'agent-1-linux' -Action Deny -Direction Outbound -RemoteIPAddress '0.0.0.0/0' -Weight 1 -Stateful $false\n"
            + "Add-VMNetworkAdapterExtendedAcl -VMName 'agent-1-linux' -Action Deny -Direction Inbound -RemoteIPAddress '0.0.0.0/0' -Weight 1 -Stateful $false\n"
            + "Add-VMNetworkAdapterExtendedAcl -VMName 'agent-1-linux' -Action Deny -Direction Outbound -RemoteIPAddress '::/0' -Weight 1 -Stateful $false\n"
            + "Add-VMNetworkAdapterExtendedAcl -VMName 'agent-1-linux' -Action Deny -Direction Inbound -RemoteIPAddress '::/0' -Weight 1 -Stateful $false\n";
        Assert.Equal(expected, script);
    }

    [Fact]
    public void HyperV_block_all_has_no_allow_rule()
    {
        var acls = HyperVAclRules.GenerateBlockAll();

        Assert.Equal(4, acls.Count);
        Assert.All(acls, a => Assert.Equal("Deny", a.Action));
    }

    [Fact]
    public void HyperV_ipv6_proxy_address_is_written_canonically()
    {
        var acls = HyperVAclRules.Generate(Rules("fd00::1", 3128));

        Assert.Equal("fd00::1", acls[0].RemoteAddress);
    }

    [Theory]
    [InlineData("vm'; Remove-Item C:\\ #")]
    [InlineData("vm\"name")]
    [InlineData("")]
    [InlineData("$(whoami)")]
    [InlineData("a`b")]
    [InlineData("vm\nname")]
    public void HyperV_vm_names_with_shell_characters_are_refused(string vmName)
    {
        Assert.Throws<ArgumentException>(() => HyperVAclRules.ToPowerShell(vmName, HyperVAclRules.GenerateBlockAll()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void Rules_need_a_bound_port(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HyperVAclRules.Generate(Rules("10.0.0.1", port)));
        Assert.Throws<ArgumentOutOfRangeException>(() => NftablesRuleset.Generate("a1", "tap0", Rules("10.0.0.1", port)));
    }

    [Fact]
    public void Nftables_ipv4_golden()
    {
        var text = NftablesRuleset.Generate("agent1", "vnet3", Rules("10.99.0.1", 3128));

        const string expected = """
            add table bridge bazaar_egress_agent1
            delete table bridge bazaar_egress_agent1
            table bridge bazaar_egress_agent1 {
              chain input {
                type filter hook input priority -200; policy accept;
                iifname "vnet3" ct state established,related accept
                iifname "vnet3" ether type arp accept
                iifname "vnet3" ip daddr 10.99.0.1 tcp dport 3128 accept
                iifname "vnet3" drop
              }
              chain output {
                type filter hook output priority -200; policy accept;
                oifname "vnet3" ct state established,related accept
                oifname "vnet3" ether type arp accept
                oifname "vnet3" ip saddr 10.99.0.1 tcp sport 3128 accept
                oifname "vnet3" drop
              }
              chain forward {
                type filter hook forward priority -200; policy accept;
                iifname "vnet3" drop
                oifname "vnet3" drop
              }
            }

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Nftables_ipv6_proxy_uses_ip6_matches()
    {
        var text = NftablesRuleset.Generate("agent1", "vnet3", Rules("fd00::1", 3128));

        Assert.Contains("iifname \"vnet3\" ip6 daddr fd00::1 tcp dport 3128 accept", text, StringComparison.Ordinal);
        Assert.Contains("oifname \"vnet3\" ip6 saddr fd00::1 tcp sport 3128 accept", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" ip daddr", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Nftables_block_all_golden()
    {
        var text = NftablesRuleset.GenerateBlockAll("agent1", "vnet3");

        const string expected = """
            add table bridge bazaar_egress_agent1
            delete table bridge bazaar_egress_agent1
            table bridge bazaar_egress_agent1 {
              chain input {
                type filter hook input priority -200; policy accept;
                iifname "vnet3" drop
              }
              chain output {
                type filter hook output priority -200; policy accept;
                oifname "vnet3" drop
              }
              chain forward {
                type filter hook forward priority -200; policy accept;
                iifname "vnet3" drop
                oifname "vnet3" drop
              }
            }

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), text);
        Assert.DoesNotContain("accept\n", text.Replace("policy accept;", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vnet3\" drop; flush ruleset; \"")]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("averyveryverylongname0")]
    public void Nftables_interface_names_with_odd_characters_are_refused(string iface)
    {
        Assert.Throws<ArgumentException>(() => NftablesRuleset.Generate("a1", iface, Rules("10.0.0.1", 3128)));
        Assert.Throws<ArgumentException>(() => NftablesRuleset.GenerateBlockAll("a1", iface));
    }

    [Theory]
    [InlineData("a1 { }")]
    [InlineData("")]
    [InlineData("a-b")]
    public void Nftables_agent_tags_with_odd_characters_are_refused(string tag)
    {
        Assert.Throws<ArgumentException>(() => NftablesRuleset.Generate(tag, "vnet3", Rules("10.0.0.1", 3128)));
    }
}
