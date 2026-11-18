using Bazaar.Contracts.Coding;
using Bazaar.Server.CodingPermissions;
using Microsoft.Extensions.DependencyInjection;
using ServerCapabilityClass = Bazaar.Server.Security.Policy.CapabilityClass;

namespace Bazaar.Server.CodingPermissions.Tests;

public sealed class ToolPermissionGateTests
{
    private static readonly string _hash = new('a', 64);

    private static ToolCallContext Ctx(
        ToolCapabilityClass cls = ToolCapabilityClass.Exec,
        SessionTaint taint = SessionTaint.None,
        string tool = "run_command",
        string? branch = null,
        string? path = null,
        string trust = "agent") =>
        new("agent-1", "s1", tool, cls, _hash, trust, taint, path, branch);

    private static (ToolPermissionGate Gate, InMemoryToolCallTrace Trace, InMemoryAgentFreeze Freeze) Make(
        Func<ToolCallContext, GenericPolicyDecision>? policy = null,
        CodingPermissionOptions? options = null)
    {
        var trace = new InMemoryToolCallTrace();
        var freeze = new InMemoryAgentFreeze();
        var gate = new ToolPermissionGate(
            policy is null ? FakeGenericToolPolicy.AllowAll() : new FakeGenericToolPolicy(policy),
            trace,
            freeze,
            options ?? new CodingPermissionOptions());
        return (gate, trace, freeze);
    }

    private static GenericPolicyDecision Allowed(bool grant = false) => new(PermissionOutcome.Allow, "ok", grant);

    [Theory]
    [InlineData(SessionTaint.None)]
    [InlineData(SessionTaint.Soft)]
    public async Task Routine_exec_is_allowed_when_untainted_or_soft(SessionTaint taint)
    {
        var (gate, trace, _) = Make();
        var d = await gate.EvaluateAsync(Ctx(taint: taint), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allow, d.Outcome);
        Assert.Single(trace.Rows);
    }

    [Fact]
    public async Task Push_to_agent_branch_is_allowed_untainted()
    {
        var (gate, _, _) = Make();
        var d = await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Push, branch: "agent/fix-x", tool: "git_push"), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allow, d.Outcome);
    }

    [Fact]
    public async Task Push_while_hard_tainted_without_approval_requires_approval()
    {
        var (gate, _, _) = Make();
        var d = await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Push, SessionTaint.Hard, "git_push", "agent/fix-x"), CancellationToken.None);
        Assert.Equal(PermissionOutcome.RequireApproval, d.Outcome);
        Assert.NotNull(d.Scope);
        Assert.True(d.Scope!.PerAction);
    }

    [Fact]
    public async Task Push_while_hard_tainted_with_grant_is_allowed()
    {
        var (gate, _, _) = Make(_ => Allowed(grant: true));
        var d = await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Push, SessionTaint.Hard, "git_push", "agent/fix-x"), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Allow, d.Outcome);
    }

    [Fact]
    public async Task Push_always_approve_tightens_untainted_push()
    {
        var (gate, _, _) = Make(options: new CodingPermissionOptions { PushAlwaysApprove = true });
        var d = await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Push, tool: "git_push", branch: "agent/fix-x"), CancellationToken.None);
        Assert.Equal(PermissionOutcome.RequireApproval, d.Outcome);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("MAIN")]
    [InlineData("feature/x")]
    [InlineData("agent/")]
    [InlineData("agent/../main")]
    [InlineData(null)]
    public async Task Push_to_default_or_non_agent_branch_is_always_denied_even_with_grant(string? branch)
    {
        var (gate, trace, _) = Make(_ => Allowed(grant: true));
        var d = await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Push, tool: "git_push", branch: branch), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Deny, d.Outcome);
        Assert.Equal(ToolCallStatus.Refused, Assert.Single(trace.Rows).Status);
    }

    [Theory]
    [InlineData(ToolCapabilityClass.Merge)]
    [InlineData(ToolCapabilityClass.Deploy)]
    [InlineData(ToolCapabilityClass.Spend)]
    [InlineData(ToolCapabilityClass.CreateAccount)]
    [InlineData(ToolCapabilityClass.GrantScope)]
    public async Task Always_approve_classes_need_a_grant_even_if_generic_policy_allows(ToolCapabilityClass cls)
    {
        var (gate, _, _) = Make();
        var d = await gate.EvaluateAsync(Ctx(cls, tool: "t"), CancellationToken.None);
        Assert.Equal(PermissionOutcome.RequireApproval, d.Outcome);

        var (granted, _, _) = Make(_ => Allowed(grant: true));
        Assert.Equal(PermissionOutcome.Allow, (await granted.EvaluateAsync(Ctx(cls, tool: "t"), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Generic_policy_approval_requirement_and_denial_pass_through()
    {
        var (gate, _, _) = Make(c => c.CapabilityClass == ToolCapabilityClass.Post
            ? new GenericPolicyDecision(PermissionOutcome.RequireApproval, "tier 2")
            : new GenericPolicyDecision(PermissionOutcome.Deny, "no"));
        Assert.Equal(PermissionOutcome.RequireApproval, (await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Post, tool: "p"), CancellationToken.None)).Outcome);
        Assert.Equal(PermissionOutcome.Deny, (await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Read, tool: "r"), CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData("load_mcp_config")]
    [InlineData("register_mcp_server")]
    [InlineData("load_instruction_file")]
    public async Task Repo_mcp_config_and_instruction_loading_is_denied(string tool)
    {
        var (gate, _, _) = Make();
        var d = await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Read, tool: tool, path: ".mcp.json"), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Deny, d.Outcome);
    }

    [Fact]
    public async Task Mcp_tool_from_repo_source_is_denied_but_plain_edit_of_claude_md_is_not()
    {
        var (gate, _, _) = Make();
        Assert.Equal(PermissionOutcome.Deny, (await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Exec, tool: "mcp__srv__do", trust: "repo"), CancellationToken.None)).Outcome);
        Assert.Equal(PermissionOutcome.Allow, (await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Exec, tool: "edit_file", path: "CLAUDE.md"), CancellationToken.None)).Outcome);
        Assert.Equal(PermissionOutcome.Deny, (await gate.EvaluateAsync(Ctx(ToolCapabilityClass.Read, tool: "load_file_as_config", path: ".claude/settings.json"), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Trace_write_failure_denies()
    {
        var (gate, trace, _) = Make();
        trace.FailWrites = true;
        var d = await gate.EvaluateAsync(Ctx(), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Deny, d.Outcome);
    }

    [Fact]
    public async Task Trace_row_is_written_before_return_with_class_taint_and_hash()
    {
        var (gate, trace, _) = Make();
        await gate.EvaluateAsync(Ctx(taint: SessionTaint.Soft), CancellationToken.None);
        var row = Assert.Single(trace.Rows);
        Assert.Equal("Exec", row.CapabilityClass);
        Assert.True(row.TaintedAtWrite);
        Assert.Equal(_hash, row.ArgsSha256);
        Assert.Equal("agent", row.SourceTrust);
    }

    [Fact]
    public async Task Rate_cap_breach_denies_and_freezes_then_stays_frozen()
    {
        var (gate, _, freeze) = Make(_ => new GenericPolicyDecision(PermissionOutcome.Deny, "quota", RateLimited: true));
        var d = await gate.EvaluateAsync(Ctx(), CancellationToken.None);
        Assert.Equal(PermissionOutcome.Deny, d.Outcome);
        Assert.True(freeze.Frozen.ContainsKey("agent-1"));

        var (gate2, _, _) = Make();
        await freeze.FreezeAsync("agent-1", "x", CancellationToken.None);
        var trace = new InMemoryToolCallTrace();
        var frozenGate = new ToolPermissionGate(FakeGenericToolPolicy.AllowAll(), trace, freeze, new CodingPermissionOptions());
        Assert.Equal(PermissionOutcome.Deny, (await frozenGate.EvaluateAsync(Ctx(), CancellationToken.None)).Outcome);
        Assert.NotNull(gate2);
    }

    [Fact]
    public async Task Throwing_policy_fails_closed()
    {
        var (gate, _, _) = Make(_ => throw new InvalidOperationException("boom"));
        Assert.Equal(PermissionOutcome.Deny, (await gate.EvaluateAsync(Ctx(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Malformed_context_and_unknown_class_are_denied()
    {
        var (gate, _, _) = Make();
        Assert.Equal(PermissionOutcome.Deny, (await gate.EvaluateAsync(Ctx() with { ArgsSha256 = "short" }, CancellationToken.None)).Outcome);
        Assert.Equal(PermissionOutcome.Deny, (await gate.EvaluateAsync(Ctx() with { Tool = " " }, CancellationToken.None)).Outcome);
        Assert.Equal(PermissionOutcome.Deny, (await gate.EvaluateAsync(Ctx((ToolCapabilityClass)999), CancellationToken.None)).Outcome);
    }

    [Fact]
    public void Options_cannot_unprotect_the_default_branch()
    {
        Assert.Throws<ArgumentException>(() => new CodingPermissionOptions { DefaultBranch = " " }.Validate());
        Assert.Throws<ArgumentException>(() => new ToolPermissionGate(
            FakeGenericToolPolicy.AllowAll(), new InMemoryToolCallTrace(), new InMemoryAgentFreeze(), new CodingPermissionOptions { DefaultBranch = "" }));
    }

    [Fact]
    public void Di_helper_registers_the_gate_and_fails_without_ports()
    {
        var services = new ServiceCollection();
        services.AddBazaarCodingPermissions();
        services.AddSingleton<IGenericToolPolicy>(FakeGenericToolPolicy.AllowAll());
        services.AddSingleton<IToolCallTrace, InMemoryToolCallTrace>();
        Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().GetRequiredService<IToolPermissionGate>());

        services.AddSingleton<IAgentFreeze, InMemoryAgentFreeze>();
        Assert.IsType<ToolPermissionGate>(services.BuildServiceProvider().GetRequiredService<IToolPermissionGate>());
    }

    // Test-only reference to Bazaar.Server: modules cannot see Server's CapabilityClass, so this one test keeps the mirror honest.
    [Fact]
    public void Tool_capability_class_matches_servers_capability_class_member_for_member()
    {
        Assert.Equal(
            Enum.GetNames<ServerCapabilityClass>(),
            Enum.GetNames<ToolCapabilityClass>());
        Assert.Equal(
            Enum.GetValues<ServerCapabilityClass>().Select(v => (int)v),
            Enum.GetValues<ToolCapabilityClass>().Select(v => (int)v));
    }
}
