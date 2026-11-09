using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Tests.Security.Policy;

namespace Bazaar.Server.Tests.Agents.Turns.Tools;

public class ToolDispatcherTests
{
    private static readonly Guid _agentId = Guid.Parse("0196a6f0-0000-7000-8000-000000000401");

    private static readonly Guid _turnId = Guid.Parse("0196a6f0-0000-7000-8000-000000000402");

    private static readonly Guid _sessionId = Guid.Parse("0196a6f0-0000-7000-8000-000000000403");

    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private const string _tool = "gc.post";

    private const string _args = """{"gc":"general","text":"hello","secret":"s3cr3t-token"}""";

    private static readonly PolicyDecision _allow = new(PolicyOutcome.Allow, PolicyReason.Autonomous);

    private static ToolDispatchRequest Request(
        string tool = _tool,
        string args = _args,
        TurnRole role = TurnRole.Actor,
        bool idempotent = false,
        string callId = "call-1") =>
        new(_agentId, _turnId, _sessionId, role, new ModelToolCall(callId, tool, args), idempotent);

    private static string Sha256Hex(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static ToolDispatcher Dispatcher(
        IToolPolicyEngine policy,
        FakeToolRegistry registry,
        FakeAuditLog audit,
        FakeInFlightCallStore? inFlight = null,
        SessionTaint? taint = null,
        ToolDispatcherOptions? options = null) =>
        new(registry, policy, new FakeSessionTaintStore(taint ?? SessionTaint.Clean), inFlight ?? new FakeInFlightCallStore(), audit, new FixedClock(_now), options);

    [Fact]
    public async Task An_allowed_call_runs_and_is_logged_by_args_hash_only()
    {
        var tool = new FakeTool(_ => Task.FromResult("""{"posted":true}"""));
        var audit = new FakeAuditLog();
        var policy = new FakePolicyEngine(_allow);

        var result = await Dispatcher(policy, new FakeToolRegistry((_tool, tool)), audit)
            .InvokeAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal("""{"posted":true}""", result.ResultJson);
        Assert.Equal(1, tool.RunCount);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal($"agent:{_agentId:D}", entry.Actor);
        Assert.Equal("tool.dispatch", entry.Kind);
        Assert.Equal(_now, entry.Ts);
        Assert.Contains(Sha256Hex(_args), entry.Payload);
        Assert.Contains($"\"turn_id\":\"{_turnId:D}\"", entry.Payload);
        Assert.Contains($"\"tool\":\"{_tool}\"", entry.Payload);
        Assert.Contains("\"outcome\":\"Executed\"", entry.Payload);
        Assert.Contains("\"reason\":\"Autonomous\"", entry.Payload);
        Assert.Contains("\"duration_ms\":", entry.Payload);

        // The raw arguments never reach the log, neither their text nor any key or value in them.
        Assert.DoesNotContain("s3cr3t-token", entry.Payload);
        Assert.DoesNotContain("hello", entry.Payload);
        Assert.DoesNotContain("\"gc\":", entry.Payload);
    }

    [Fact]
    public async Task The_policy_sees_the_parsed_args_taint_and_mapped_role()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var policy = new FakePolicyEngine(_allow);
        var taint = PolicyFixtures.Soft();

        await Dispatcher(policy, new FakeToolRegistry((_tool, tool)), new FakeAuditLog(), taint: taint)
            .InvokeAsync(Request(), CancellationToken.None);

        var context = Assert.Single(policy.Seen);
        Assert.Equal(_agentId.ToString("D"), context.AgentId);
        Assert.Equal(_sessionId, context.SessionId);
        Assert.Equal(SessionRoles.Actor, context.Role);
        Assert.Equal(_tool, context.Tool);
        Assert.Equal("general", context.Args.GetProperty("gc").GetString());
        Assert.Equal(taint, context.Taint);
        Assert.Equal(_now, context.Now);
    }

    [Fact]
    public async Task A_contextual_tool_gets_the_verified_caller_and_the_plain_call_is_not_used()
    {
        var tool = new FakeContextualTool();
        var policy = new FakePolicyEngine(_allow);

        var result = await Dispatcher(policy, new FakeToolRegistry((_tool, tool)), new FakeAuditLog())
            .InvokeAsync(Request(), CancellationToken.None);

        // The plain overload throws on a contextual tool, which would surface as the fixed tool-failed result.
        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal("""{"ok":true}""", result.ResultJson);
        Assert.Equal(new ToolCallContext(_agentId, _sessionId, _turnId), tool.LastContext);
    }

    private sealed class FakeContextualTool : IContextualTool
    {
        public ToolCallContext? LastContext { get; private set; }

        public Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.FromResult("""{"ok":true}""");
        }
    }

    [Fact]
    public async Task A_require_approval_decision_returns_pending_and_does_not_run()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var audit = new FakeAuditLog();
        var policy = new FakePolicyEngine(new PolicyDecision(PolicyOutcome.RequireApproval, PolicyReason.OwnerApprovalRequired));

        var result = await Dispatcher(policy, new FakeToolRegistry((_tool, tool)), audit)
            .InvokeAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.PendingApproval, result.Outcome);
        Assert.Equal("OwnerApprovalRequired", result.Reason);
        Assert.Null(result.ResultJson);
        Assert.Equal(0, tool.RunCount);
        Assert.Contains("\"outcome\":\"PendingApproval\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public async Task A_deny_decision_returns_denied_and_does_not_run()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var audit = new FakeAuditLog();
        var policy = new FakePolicyEngine(new PolicyDecision(PolicyOutcome.Deny, PolicyReason.TierDenied));

        var result = await Dispatcher(policy, new FakeToolRegistry((_tool, tool)), audit)
            .InvokeAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Denied, result.Outcome);
        Assert.Equal("TierDenied", result.Reason);
        Assert.Equal(0, tool.RunCount);
        Assert.Contains("\"outcome\":\"Denied\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public async Task A_reader_turn_is_denied_a_write_tool_before_policy_is_asked()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var audit = new FakeAuditLog();
        var policy = new FakePolicyEngine(_allow);

        var result = await Dispatcher(policy, new FakeToolRegistry((_tool, tool)), audit)
            .InvokeAsync(Request(role: TurnRole.Reader), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Denied, result.Outcome);
        Assert.Equal("reader turn", result.Reason);
        Assert.Equal(0, tool.RunCount);
        Assert.Empty(policy.Seen);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task A_reader_turn_may_call_a_tool_named_in_the_read_only_option()
    {
        var tool = new FakeTool(_ => Task.FromResult("""{"page":"ok"}"""));
        var options = new ToolDispatcherOptions { ReaderReadOnlyTools = new HashSet<string>(["web.fetch"], StringComparer.Ordinal) };
        var policy = new FakePolicyEngine(new PolicyDecision(PolicyOutcome.Allow, PolicyReason.Autonomous));

        var result = await Dispatcher(policy, new FakeToolRegistry(("web.fetch", tool)), new FakeAuditLog(), options: options)
            .InvokeAsync(Request(tool: "web.fetch", role: TurnRole.Reader), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal(1, tool.RunCount);
        Assert.Equal(SessionRoles.Reader, Assert.Single(policy.Seen).Role);
    }

    [Fact]
    public void The_default_options_leave_the_reader_read_only_set_empty()
    {
        Assert.Empty(ToolDispatcherOptions.Default.ReaderReadOnlyTools);
    }

    [Fact]
    public async Task An_unknown_tool_is_denied_before_policy_is_asked()
    {
        var audit = new FakeAuditLog();
        var policy = new FakePolicyEngine(_allow);

        var result = await Dispatcher(policy, new FakeToolRegistry(), audit)
            .InvokeAsync(Request(tool: "does.not.exist"), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Denied, result.Outcome);
        Assert.Equal("unknown tool", result.Reason);
        Assert.Empty(policy.Seen);
        Assert.Contains("\"reason\":\"unknown tool\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public async Task Malformed_arguments_are_denied_and_logged()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var audit = new FakeAuditLog();

        var result = await Dispatcher(new FakePolicyEngine(_allow), new FakeToolRegistry((_tool, tool)), audit)
            .InvokeAsync(Request(args: "{not json"), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Denied, result.Outcome);
        Assert.Equal("malformed arguments", result.Reason);
        Assert.Equal(0, tool.RunCount);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task An_in_flight_non_idempotent_call_is_unknown_and_is_not_rerun()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var inFlight = new FakeInFlightCallStore();
        var audit = new FakeAuditLog();
        var registry = new FakeToolRegistry((_tool, tool));

        // The first attempt is cancelled mid-call, which leaves the in-flight marker open, as a crash would.
        var crashing = new FakeTool(_ => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Dispatcher(new FakePolicyEngine(_allow), new FakeToolRegistry((_tool, crashing)), audit, inFlight)
                .InvokeAsync(Request(), CancellationToken.None));

        Assert.Equal(1, inFlight.OpenCount);

        var result = await Dispatcher(new FakePolicyEngine(_allow), registry, audit, inFlight)
            .InvokeAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Unknown, result.Outcome);
        Assert.Equal("in flight at crash", result.Reason);
        Assert.Equal(0, tool.RunCount);
        Assert.Contains("\"outcome\":\"Unknown\"", audit.Entries[^1].Payload);
    }

    [Fact]
    public async Task An_in_flight_idempotent_call_runs_again()
    {
        var inFlight = new FakeInFlightCallStore();
        var audit = new FakeAuditLog();

        var crashing = new FakeTool(_ => throw new OperationCanceledException());
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Dispatcher(new FakePolicyEngine(_allow), new FakeToolRegistry((_tool, crashing)), audit, inFlight)
                .InvokeAsync(Request(idempotent: true), CancellationToken.None));

        Assert.Equal(1, inFlight.OpenCount);

        var tool = new FakeTool(_ => Task.FromResult("""{"ok":true}"""));
        var result = await Dispatcher(new FakePolicyEngine(_allow), new FakeToolRegistry((_tool, tool)), audit, inFlight)
            .InvokeAsync(Request(idempotent: true), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal(1, tool.RunCount);
        Assert.Equal(0, inFlight.OpenCount);
    }

    [Fact]
    public async Task A_tool_exception_becomes_an_error_result_and_is_logged_without_its_message()
    {
        const string leakyMessage = @"C:\Users\ops\vault\token=abc123 /home/ops/.ssh/id_ed25519";
        var tool = new FakeTool(_ => Task.FromException<string>(new InvalidOperationException(leakyMessage)));
        var inFlight = new FakeInFlightCallStore();
        var audit = new FakeAuditLog();

        var result = await Dispatcher(new FakePolicyEngine(_allow), new FakeToolRegistry((_tool, tool)), audit, inFlight)
            .InvokeAsync(Request(), CancellationToken.None);

        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        Assert.Equal("""{"error":"tool failed"}""", result.ResultJson);
        Assert.Null(result.Reason);

        // The failed call is complete, not in flight: it returned, with a failure.
        Assert.Equal(0, inFlight.OpenCount);

        var entry = Assert.Single(audit.Entries);
        Assert.Contains("\"reason\":\"tool_exception:InvalidOperationException\"", entry.Payload);
        Assert.Contains("\"outcome\":\"Executed\"", entry.Payload);
        Assert.DoesNotContain("abc123", entry.Payload);
        Assert.DoesNotContain("ssh", entry.Payload);
        Assert.DoesNotContain(leakyMessage, result.ResultJson);
    }

    [Fact]
    public async Task Cancellation_is_rethrown_and_logged_as_unknown()
    {
        var tool = new FakeTool(_ => throw new OperationCanceledException());
        var audit = new FakeAuditLog();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Dispatcher(new FakePolicyEngine(_allow), new FakeToolRegistry((_tool, tool)), audit)
                .InvokeAsync(Request(), CancellationToken.None));

        Assert.Contains("\"outcome\":\"Unknown\"", Assert.Single(audit.Entries).Payload);
        Assert.Contains("\"reason\":\"cancelled\"", audit.Entries[0].Payload);
    }

    [Fact]
    public void The_registry_rejects_a_repeated_or_blank_name()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));

        Assert.Throws<ArgumentException>(() => new ToolRegistry([(_tool, tool), (_tool, tool)]));
        Assert.Throws<ArgumentException>(() => new ToolRegistry([(" ", tool)]));
    }

    [Fact]
    public void The_registry_finds_a_registered_tool_by_exact_name()
    {
        var tool = new FakeTool(_ => Task.FromResult("{}"));
        var registry = new ToolRegistry([(_tool, tool)]);

        Assert.True(registry.TryGet(_tool, out var found));
        Assert.Same(tool, found);
        Assert.False(registry.TryGet("GC.POST", out _));
    }
}
