using System.Text.Json;
using Bazaar.Server.VmEgress.Rules;

namespace Bazaar.Server.VmEgress.Enforcers.Tests;

public class HyperVEgressEnforcerTests
{
    private static (HyperVEgressEnforcer Enforcer, HyperVSim Runner) Create(params string[] vms)
    {
        var runner = new HyperVSim();
        return (new HyperVEgressEnforcer(new FakeResolver(new EgressTarget(vms)), runner), runner);
    }

    [Fact]
    public async Task Apply_runs_the_constant_apply_script_then_the_constant_read_back_script()
    {
        var (enforcer, runner) = Create("agent-vm-1");

        await enforcer.ApplyAsync("a1", Samples.Rules());

        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal(HyperVEgressScripts.Apply, runner.Commands[0].StandardInput.TrimEnd());
        Assert.Equal(HyperVEgressScripts.ReadBack, runner.Commands[1].StandardInput.TrimEnd());
        Assert.All(runner.Commands, c =>
        {
            Assert.Equal(HostTool.PowerShell, c.Tool);
            Assert.Equal(["-NoProfile", "-NonInteractive", "-Command", "-"], c.Arguments);
        });
    }

    [Fact]
    public async Task Names_and_rules_travel_only_in_the_parameter_variable_never_in_script_or_argv()
    {
        var (enforcer, runner) = Create("agent-vm-1");

        await enforcer.ApplyAsync("a1", Samples.Rules(4040));

        var apply = runner.Commands[0];
        Assert.DoesNotContain("agent-vm-1", apply.StandardInput);
        Assert.DoesNotContain("4040", apply.StandardInput);
        Assert.DoesNotContain("agent-vm-1", string.Join(' ', apply.Arguments));
        var env = Assert.Single(apply.Environment!);
        Assert.Equal(HyperVEgressScripts.ParametersVariable, env.Key);
        using var p = JsonDocument.Parse(env.Value);
        Assert.Equal("agent-vm-1", p.RootElement.GetProperty("VmName").GetString());
        Assert.Equal(HyperVEgressEnforcer.RulesJson(HyperVAclRules.Generate(Samples.Rules(4040))), p.RootElement.GetProperty("Rules").GetString());
        using var readBack = JsonDocument.Parse(runner.Commands[1].Environment![HyperVEgressScripts.ParametersVariable]);
        Assert.Equal("agent-vm-1", readBack.RootElement.GetProperty("VmName").GetString());
    }

    [Fact]
    public void Scripts_are_free_of_interpolation_markers_and_name_the_expected_cmdlets()
    {
        foreach (var script in new[] { HyperVEgressScripts.Apply, HyperVEgressScripts.ReadBack })
        {
            Assert.DoesNotContain("{{", script);
            Assert.DoesNotContain("agent-vm", script);
            Assert.Contains("$env:BAZAAR_EGRESS_PARAMS", script);
        }

        Assert.Contains("Get-VMNetworkAdapterExtendedAcl", HyperVEgressScripts.Apply);
        Assert.Contains("Add-VMNetworkAdapterExtendedAcl", HyperVEgressScripts.Apply);
        Assert.Contains("Remove-VMNetworkAdapterExtendedAcl", HyperVEgressScripts.Apply);
        Assert.Contains("Get-VMNetworkAdapterExtendedAcl", HyperVEgressScripts.ReadBack);
        Assert.DoesNotContain("Remove-", HyperVEgressScripts.ReadBack);
        Assert.DoesNotContain("Add-", HyperVEgressScripts.ReadBack);
    }

    [Fact]
    public void Apply_script_adds_deny_rules_before_it_removes_anything_and_allows_come_last()
    {
        var script = HyperVEgressScripts.Apply;
        var step1 = script.IndexOf(HyperVEgressScripts.StepDenyAdded, StringComparison.Ordinal);
        var step2 = script.IndexOf(HyperVEgressScripts.StepStaleRemoved, StringComparison.Ordinal);
        var step3 = script.IndexOf(HyperVEgressScripts.StepAllowsAdded, StringComparison.Ordinal);

        Assert.True(0 < step1 && step1 < step2 && step2 < step3);
        Assert.Equal(-1, script[..step2].IndexOf("| Remove-VMNetworkAdapterExtendedAcl", StringComparison.Ordinal));
        Assert.True(script.IndexOf("| Remove-VMNetworkAdapterExtendedAcl", StringComparison.Ordinal) > step2);
        Assert.Contains("$_.Action -eq 'Deny'", script[step1..step2]);
        Assert.Contains("$_.Action -ne 'Deny'", script[step3..]);
        Assert.Contains("[Console]::Error.WriteLine($_.Exception.Message); exit 1", script);
    }

    [Fact]
    public async Task Apply_generates_the_proxy_allows_and_the_four_deny_all_rules()
    {
        var (enforcer, runner) = Create("vm1");

        await enforcer.ApplyAsync("a1", Samples.Rules());

        var acls = runner.AclsOf("vm1");
        Assert.Equal(6, acls.Count);
        Assert.Equal(2, acls.Count(a => a.Action == "Allow" && a.RemoteAddress == "10.20.0.1" && a.RemotePort == 3128 && a.Protocol == "TCP"));
        Assert.Equal(4, acls.Count(a => a is { Action: "Deny", Weight: HyperVAclRules.DenyWeight }));
    }

    [Fact]
    public async Task Apply_replaces_and_is_idempotent()
    {
        var (enforcer, runner) = Create("vm1");

        await enforcer.ApplyAsync("a1", Samples.Rules(3128));
        await enforcer.ApplyAsync("a1", Samples.Rules(4000));
        await enforcer.ApplyAsync("a1", Samples.Rules(4000));

        var acls = runner.AclsOf("vm1");
        Assert.Equal(6, acls.Count);
        Assert.DoesNotContain(acls, a => a.RemotePort == 3128);
        Assert.Equal(2, acls.Count(a => a.RemotePort == 4000));
    }

    [Fact]
    public async Task Apply_runs_on_every_vm_of_the_agent()
    {
        var (enforcer, runner) = Create("vm-win", "vm-lin");

        await enforcer.ApplyAsync("a1", Samples.Rules());

        Assert.Equal(6, runner.AclsOf("vm-win").Count);
        Assert.Equal(6, runner.AclsOf("vm-lin").Count);
        Assert.Equal(4, runner.Commands.Count);
    }

    [Theory]
    [InlineData("vm'; Remove-VM * -Force; '")]
    [InlineData("vm\nRemove-VM x")]
    [InlineData("vm\n")]
    [InlineData("")]
    [InlineData(" vm")]
    [InlineData("vm with space")]
    [InlineData("..\\evil")]
    [InlineData("$(calc)")]
    [InlineData("vm`nx")]
    [InlineData("-VMName")]
    [InlineData("vm|x")]
    [InlineData("vmé")]
    public async Task Hostile_or_malformed_vm_names_are_rejected_before_any_command_runs(string name)
    {
        var (enforcer, runner) = Create("good-vm", name);

        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.BlockAllAsync("a1"));

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task An_agent_with_no_vm_cannot_be_enforced()
    {
        var (enforcer, runner) = Create();

        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.BlockAllAsync("a1"));

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Apply_throws_when_the_read_back_lacks_a_rule()
    {
        var (enforcer, runner) = Create("vm1");
        runner.Tamper = (_, rules) => [.. rules.Where(r => !(r.Action == "Deny" && r.Direction == "Inbound" && r.RemoteAddress == "::/0"))];

        var e = await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));

        Assert.Contains("do not match", e.Message);
    }

    [Fact]
    public async Task Apply_throws_when_the_read_back_has_an_extra_allow()
    {
        var (enforcer, runner) = Create("vm1");
        runner.Tamper = (_, rules) => [.. rules, new HyperVAclRule("Allow", "Outbound", "0.0.0.0/0", null, null, 50, true)];

        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
    }

    [Fact]
    public async Task Apply_throws_when_a_rule_differs_in_port_weight_or_statefulness()
    {
        foreach (var change in new Func<HyperVAclRule, HyperVAclRule>[]
        {
            r => r with { RemotePort = 9999 },
            r => r with { Weight = r.Weight + 1 },
            r => r with { Stateful = !r.Stateful },
            r => r with { Action = r.Action == "Allow" ? "Deny" : "Allow" },
        })
        {
            var (enforcer, runner) = Create("vm1");
            runner.Tamper = (_, rules) => [change(rules[0]), .. rules.Skip(1)];

            await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Action\":\"Deny\"}")]
    [InlineData("[]")]
    public async Task Apply_throws_on_an_unusable_read_back(string output)
    {
        var (enforcer, runner) = Create("vm1");
        var inner = runner.Handler;
        runner.Handler = c => c.StandardInput.TrimEnd() == HyperVEgressScripts.ReadBack ? new HostCommandResult(0, output, string.Empty) : inner(c);

        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
    }

    [Fact]
    public async Task Apply_throws_when_the_apply_script_fails_and_does_not_read_back()
    {
        var runner = new FakeHostRunner { Handler = _ => new HostCommandResult(1, string.Empty, "Access denied") };
        var enforcer = new HyperVEgressEnforcer(new FakeResolver(new EgressTarget(["vm1"])), runner);

        var e = await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));

        Assert.Contains("Access denied", e.Message);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task Apply_throws_when_the_read_back_script_fails()
    {
        var (enforcer, runner) = Create("vm1");
        var inner = runner.Handler;
        runner.Handler = c => c.StandardInput.TrimEnd() == HyperVEgressScripts.ReadBack ? new HostCommandResult(2, string.Empty, "boom") : inner(c);

        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
    }

    [Fact]
    public async Task A_runner_exception_surfaces()
    {
        var runner = new FakeHostRunner { Handler = _ => throw new EgressEnforcementException("timed out") };
        var enforcer = new HyperVEgressEnforcer(new FakeResolver(new EgressTarget(["vm1"])), runner);

        await Assert.ThrowsAsync<EgressEnforcementException>(() => enforcer.ApplyAsync("a1", Samples.Rules()));
        await Assert.ThrowsAnyAsync<Exception>(() => enforcer.BlockAllAsync("a1"));
    }

    [Fact]
    public async Task BlockAll_applies_deny_all_in_both_directions_for_both_families_with_no_allow()
    {
        var (enforcer, runner) = Create("vm1");
        await enforcer.ApplyAsync("a1", Samples.Rules());

        await enforcer.BlockAllAsync("a1");

        var acls = runner.AclsOf("vm1");
        Assert.Equal(4, acls.Count);
        Assert.All(acls, a => Assert.Equal("Deny", a.Action));
        Assert.Equivalent(
            new[] { ("Inbound", "0.0.0.0/0"), ("Outbound", "0.0.0.0/0"), ("Inbound", "::/0"), ("Outbound", "::/0") },
            acls.Select(a => (a.Direction, a.RemoteAddress)));
        using var p = JsonDocument.Parse(runner.Commands[^2].Environment![HyperVEgressScripts.ParametersVariable]);
        Assert.Equal(HyperVEgressEnforcer.RulesJson(HyperVAclRules.GenerateBlockAll()), p.RootElement.GetProperty("Rules").GetString());
    }

    [Fact]
    public async Task BlockAll_throws_when_an_allow_survives_in_the_read_back()
    {
        var (enforcer, runner) = Create("vm1");
        runner.Tamper = (_, rules) => [.. rules, new HyperVAclRule("Allow", "Outbound", "10.20.0.1", "TCP", 3128, 100, true)];

        var e = await Assert.ThrowsAsync<AggregateException>(() => enforcer.BlockAllAsync("a1"));
        Assert.IsType<EgressEnforcementException>(Assert.Single(e.InnerExceptions));
    }

    [Fact]
    public async Task BlockAll_tries_every_vm_then_reports_the_failures_together()
    {
        var (enforcer, runner) = Create("vm1", "vm2", "vm3");
        runner.Tamper = (vm, rules) => vm == "vm1" ? [] : rules;

        var e = await Assert.ThrowsAsync<AggregateException>(() => enforcer.BlockAllAsync("a1"));

        Assert.Single(e.InnerExceptions);
        Assert.Equal(4, runner.AclsOf("vm2").Count);
        Assert.Equal(4, runner.AclsOf("vm3").Count);
    }

    [Fact]
    public async Task Calls_for_one_agent_do_not_overlap()
    {
        var (enforcer, runner) = Create("vm1");
        runner.Delay = TimeSpan.FromMilliseconds(20);

        await Task.WhenAll(
            enforcer.ApplyAsync("a1", Samples.Rules()),
            enforcer.BlockAllAsync("a1"),
            enforcer.ApplyAsync("a1", Samples.Rules(4000)));

        Assert.Equal(1, runner.MaxConcurrent);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var (enforcer, _) = Create("vm1");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enforcer.ApplyAsync("a1", Samples.Rules(), cts.Token));
    }

    [Fact]
    public async Task Apply_rejects_an_unbound_proxy_port()
    {
        var (enforcer, runner) = Create("vm1");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => enforcer.ApplyAsync("a1", Samples.Rules(0)));

        Assert.Empty(runner.Commands);
    }

    // Needs the NUC (real Hyper-V). Skipped by default; set BAZAAR_HYPERV_TEST_VM to a throwaway VM and remove the Skip to run.
    [Fact(Skip = "Needs a real Hyper-V host (NUC, Category=Hyperv).")]
    [Trait("Category", "Hyperv")]
    public async Task Real_host_apply_and_block_round_trip()
    {
        var vm = Environment.GetEnvironmentVariable("BAZAAR_HYPERV_TEST_VM") ?? "bazaar-egress-test";
        var enforcer = new HyperVEgressEnforcer(new FakeResolver(new EgressTarget([vm])), new ProcessHostCommandRunner());

        await enforcer.ApplyAsync("hyperv-test", Samples.Rules());
        await enforcer.BlockAllAsync("hyperv-test");
    }
}
