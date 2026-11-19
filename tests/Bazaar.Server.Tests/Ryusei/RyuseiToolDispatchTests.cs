using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Tests.Agents.Turns.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The four RyuseiFuda agent tools run through the real <see cref="ToolDispatcher"/> and the real
/// <see cref="ToolPolicyEngine"/>, with the capabilities the component registers (doc 38, WP-R9). Every case here refuses
/// before the trigger service would open a connection, so no database is needed. Schedule, pause and cancel against Postgres
/// are covered by the database tests, which need Docker and run in CI only.
/// </summary>
public sealed class RyuseiToolDispatchTests
{
    private static readonly Guid _project = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _agent = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _stranger = Guid.Parse("cccccccc-cccc-7ccc-8ccc-cccccccccccc");
    private static readonly Guid _unknownTarget = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");
    private static readonly Guid _trigger = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid _session = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly Guid _turn = Guid.Parse("44444444-4444-7444-8444-444444444444");
    private static readonly DateTimeOffset _now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private const string _validSchedule = """
        {
          "kind": "interval",
          "purpose": "check the build",
          "targets": ["aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"],
          "spec": {"every": "PT30M", "anchor": "2026-11-08T12:00:00Z"}
        }
        """;

    /// <summary>A directory where each listed agent is known; the caller and targets are resolved from it.</summary>
    private sealed class KnownAgents(params Guid[] agents) : IRyuseiAgentDirectory
    {
        public Task<RyuseiAgentLookup> ResolveAsync(Guid callerAgentId, IReadOnlyCollection<Guid> targetAgentIds, CancellationToken ct)
        {
            RyuseiActor? caller = agents.Contains(callerAgentId) ? RyuseiActor.ForAgent(callerAgentId, _project) : null;
            var targets = targetAgentIds
                .Where(agents.Contains)
                .Distinct()
                .Select(id => new RyuseiTargetInfo(id, _project, true))
                .ToList();
            return Task.FromResult(new RyuseiAgentLookup(caller, targets));
        }
    }

    /// <summary>
    /// The real dispatcher over the four tools, the capabilities from the component's registrations, and the in-memory
    /// rate buckets. The trigger service points at a closed port, so reaching it would throw, and the tool would then
    /// return the fixed "tool failed" result. The assertions rule that out.
    /// </summary>
    private static (ToolDispatcher Dispatcher, FakeAuditLog Audit) Build(IRyuseiAgentDirectory directory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Ryusei:ProjectId"] = _project.ToString() })
            .Build();
        var services = new ServiceCollection();
        new RyuseiScheduleComponent().AddServices(services, configuration);
        var capabilities = services
            .Where(d => d.ServiceType == typeof(ToolCapability))
            .Select(d => (ToolCapability)d.ImplementationInstance!);

        var service = new RyuseiTriggerService(
            NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1"),
            new CapturingAuditLog());
        var registry = new FakeToolRegistry(
            (RyuseiToolContract.ScheduleToolName, new RyuseiScheduleTool(service, directory, TimeProvider.System)),
            (RyuseiToolContract.ListToolName, new RyuseiListTool(service, directory)),
            (RyuseiToolContract.PauseToolName, new RyuseiPauseTool(service, directory, TimeProvider.System)),
            (RyuseiToolContract.CancelToolName, new RyuseiCancelTool(service, directory, TimeProvider.System)));
        var policy = new ToolPolicyEngine(CapabilityRegistry.Create(capabilities), new InMemoryRateBucketStore(), PolicyOptions.Default);
        var audit = new FakeAuditLog();
        var dispatcher = new ToolDispatcher(
            registry,
            policy,
            new FakeSessionTaintStore(Bazaar.Server.Security.Taint.SessionTaint.Clean),
            new FakeInFlightCallStore(),
            audit,
            new FixedClock(_now));
        return (dispatcher, audit);
    }

    private static ToolDispatchRequest Request(string tool, string args, TurnRole role = TurnRole.Actor, Guid? agent = null) =>
        new(agent ?? _agent, _turn, _session, role, new ModelToolCall("call-1", tool, args), false, 0);

    private static JsonNode ParseResult(ToolDispatchResult result)
    {
        Assert.Equal(ToolDispatchOutcome.Executed, result.Outcome);
        return JsonNode.Parse(result.ResultJson!)!;
    }

    [Fact]
    public async Task A_list_from_a_caller_outside_the_directory_runs_through_the_dispatcher_and_is_audited()
    {
        var (dispatcher, audit) = Build(new KnownAgents(_agent));

        var result = await dispatcher.InvokeAsync(
            Request(RyuseiToolContract.ListToolName, "{}", agent: _stranger),
            TestContext.Current.CancellationToken);

        var json = ParseResult(result);
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.False(json["executed"]!.GetValue<bool>());
        Assert.Equal("NotAuthorized", json["refusal"]!.GetValue<string>());

        var entry = Assert.Single(audit.Entries);
        Assert.Equal($"agent:{_stranger:D}", entry.Actor);
        Assert.Contains("\"tool\":\"ryusei.list\"", entry.Payload);
        Assert.Contains("\"outcome\":\"Executed\"", entry.Payload);
    }

    [Fact]
    public async Task A_reader_turn_cannot_schedule_and_the_tool_does_not_run()
    {
        var (dispatcher, audit) = Build(new KnownAgents(_agent, _unknownTarget));

        var result = await dispatcher.InvokeAsync(
            Request(RyuseiToolContract.ScheduleToolName, _validSchedule, TurnRole.Reader),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolDispatchOutcome.Denied, result.Outcome);
        Assert.Null(result.ResultJson);
        Assert.Equal("reader turn", result.Reason);
        Assert.Contains("\"outcome\":\"Denied\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public async Task A_schedule_with_a_spec_that_does_not_match_its_kind_is_invalid_arguments_before_the_service()
    {
        var (dispatcher, audit) = Build(new KnownAgents(_agent));
        const string args = """
            {"kind":"interval","purpose":"check","targets":["aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"],"spec":{"every":"PT30M"}}
            """;

        var result = await dispatcher.InvokeAsync(Request(RyuseiToolContract.ScheduleToolName, args), TestContext.Current.CancellationToken);

        var json = ParseResult(result);
        Assert.Equal("invalid_arguments", json["status"]!.GetValue<string>());
        Assert.False(json["executed"]!.GetValue<bool>());
        Assert.DoesNotContain("tool failed", result.ResultJson!, StringComparison.Ordinal);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task A_schedule_that_names_an_unknown_target_is_refused_and_does_not_run()
    {
        var (dispatcher, audit) = Build(new KnownAgents(_agent));
        var args = _validSchedule.Replace("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa", _unknownTarget.ToString("D"), StringComparison.Ordinal);

        var result = await dispatcher.InvokeAsync(Request(RyuseiToolContract.ScheduleToolName, args), TestContext.Current.CancellationToken);

        var json = ParseResult(result);
        Assert.False(json["ok"]!.GetValue<bool>());
        Assert.False(json["executed"]!.GetValue<bool>());
        Assert.DoesNotContain("tool failed", result.ResultJson!, StringComparison.Ordinal);
        Assert.Single(audit.Entries);
    }

    [Theory]
    [InlineData(RyuseiToolContract.PauseToolName)]
    [InlineData(RyuseiToolContract.CancelToolName)]
    public async Task A_pause_or_cancel_without_a_trigger_id_is_invalid_arguments(string tool)
    {
        var (dispatcher, audit) = Build(new KnownAgents(_agent));

        var result = await dispatcher.InvokeAsync(Request(tool, "{}"), TestContext.Current.CancellationToken);

        var json = ParseResult(result);
        Assert.Equal("invalid_arguments", json["status"]!.GetValue<string>());
        Assert.False(json["executed"]!.GetValue<bool>());
        Assert.Contains($"\"tool\":\"{tool}\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public async Task A_pause_by_a_caller_outside_the_directory_is_refused_without_reaching_the_service()
    {
        var (dispatcher, _) = Build(new KnownAgents(_agent));
        var args = $$"""{"trigger_id":"{{_trigger:D}}"}""";

        var result = await dispatcher.InvokeAsync(
            Request(RyuseiToolContract.PauseToolName, args, agent: _stranger),
            TestContext.Current.CancellationToken);

        var json = ParseResult(result);
        Assert.False(json["executed"]!.GetValue<bool>());
        Assert.Equal("NotAuthorized", json["refusal"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_ryusei_name_that_is_not_one_of_the_four_is_denied_as_an_unknown_tool()
    {
        var (dispatcher, audit) = Build(new KnownAgents(_agent));

        var result = await dispatcher.InvokeAsync(Request("ryusei.delete", "{}"), TestContext.Current.CancellationToken);

        Assert.Equal(ToolDispatchOutcome.Denied, result.Outcome);
        Assert.Equal("unknown tool", result.Reason);
        Assert.Contains("\"outcome\":\"Denied\"", Assert.Single(audit.Entries).Payload);
    }

    [Fact]
    public void Every_required_field_in_each_advertised_schema_is_enforced_by_the_parser()
    {
        var schedule = Definition(RyuseiToolContract.ScheduleToolName);
        var required = RequiredNames(schedule);
        Assert.Equal(["kind", "purpose", "spec", "targets"], required.Order(StringComparer.Ordinal));

        using var valid = JsonDocument.Parse(_validSchedule);
        Assert.True(RyuseiToolContract.ParseSchedule(valid.RootElement).IsValid);

        foreach (var name in required)
        {
            var node = JsonNode.Parse(_validSchedule)!.AsObject();
            node.Remove(name);
            using var doc = JsonDocument.Parse(node.ToJsonString());
            Assert.False(RyuseiToolContract.ParseSchedule(doc.RootElement).IsValid, $"Missing {name} must be refused.");
        }

        foreach (var tool in new[] { RyuseiToolContract.PauseToolName, RyuseiToolContract.CancelToolName })
        {
            Assert.Equal(["trigger_id"], RequiredNames(Definition(tool)));
        }

        Assert.Empty(RequiredNames(Definition(RyuseiToolContract.ListToolName)));
    }

    [Fact]
    public void The_enum_values_in_each_schema_are_exactly_the_values_the_parser_accepts()
    {
        var schedule = Definition(RyuseiToolContract.ScheduleToolName);
        var properties = schedule["properties"]!;

        Assert.Equal(["event", "interval", "timer", "watcher"], EnumOf(properties["kind"]!).Order(StringComparer.Ordinal));
        Assert.Equal(["attach", "new_gc", "none"], EnumOf(properties["ping_mode"]!).Order(StringComparer.Ordinal));

        var predicateKinds = EnumOf(properties["spec"]!["properties"]!["predicate"]!["properties"]!["kind"]!);
        var parserKinds = Enum.GetValues<RyuseiPredicateKind>().Select(RyuseiPredicates.WireName);
        Assert.Equal(parserKinds.Order(StringComparer.Ordinal), predicateKinds.Order(StringComparer.Ordinal));

        var list = Definition(RyuseiToolContract.ListToolName);
        Assert.Equal(["active", "cancelled", "expired", "fired", "paused"], EnumOf(list["properties"]!["state"]!).Order(StringComparer.Ordinal));
        Assert.Equal(RyuseiToolContract.MaxListLimit, list["properties"]!["limit"]!["maximum"]!.GetValue<int>());
        Assert.Equal(1, list["properties"]!["limit"]!["minimum"]!.GetValue<int>());

        // Every advertised kind parses with a spec of that kind, and every advertised state parses.
        var specs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["timer"] = """{"fireAt":"2026-11-12T12:00:00Z"}""",
            ["interval"] = """{"every":"PT30M","anchor":"2026-11-08T12:00:00Z"}""",
            ["watcher"] = """{"predicate":{"kind":"gc.message"},"pollSeconds":60,"debounceSeconds":30}""",
            ["event"] = """{"predicate":{"kind":"task.transitioned"}}""",
        };
        Assert.Equal(specs.Keys.Order(StringComparer.Ordinal), EnumOf(properties["kind"]!).Order(StringComparer.Ordinal));
        foreach (var (kind, spec) in specs)
        {
            using var doc = JsonDocument.Parse($$"""{"kind":"{{kind}}","purpose":"p","targets":["{{_agent:D}}"],"spec":{{spec}}}""");
            Assert.True(RyuseiToolContract.ParseSchedule(doc.RootElement).IsValid, $"kind {kind} must parse.");
        }

        foreach (var state in EnumOf(list["properties"]!["state"]!))
        {
            using var doc = JsonDocument.Parse($$"""{"state":"{{state}}"}""");
            Assert.True(RyuseiToolContract.ParseList(doc.RootElement).IsValid, $"state {state} must parse.");
        }
    }

    [Fact]
    public void A_refusal_result_says_the_call_did_not_run_and_a_successful_one_says_it_did()
    {
        Assert.False(RyuseiToolContract.BuildRefusal("kind is required.")["executed"]!.GetValue<bool>());
        Assert.False(RyuseiToolContract.BuildResult(RyuseiTriggerResult.Refused(RyuseiRefusal.NotAuthorized, "No."))["executed"]!.GetValue<bool>());
        Assert.False(RyuseiToolContract.BuildResult(RyuseiTriggerResult.Other(RyuseiTriggerStatus.NotFound, "Gone."))["executed"]!.GetValue<bool>());
        Assert.True(RyuseiToolContract.BuildResult(RyuseiTriggerResult.Ok(_trigger, 2))["executed"]!.GetValue<bool>());
    }

    private static JsonNode Definition(string name)
    {
        var definition = RyuseiToolContract.Definitions.Single(d => d.Name == name);
        return JsonNode.Parse(definition.ParametersJsonSchema)!;
    }

    private static List<string> RequiredNames(JsonNode schema) =>
        schema["required"] is JsonArray required
            ? required.Select(n => n!.GetValue<string>()).ToList()
            : [];

    private static List<string> EnumOf(JsonNode property) =>
        property["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
}
