using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.RyuseiFuda.Orrery;
using Bazaar.Server.Agents.Turns.Tools;
using Npgsql;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// The four RyuseiFuda agent tool adapters (WP-R9) with an in-memory agent directory. The trigger service is a concrete class
/// over a database, so these tests cover only paths that refuse before the service is called (no connection is opened),
/// plus the list result shape. Creation, pause, cancel and list against Postgres are covered by the database tests, which
/// need Docker.
/// </summary>
public sealed class RyuseiToolAdaptersTests
{
    private static readonly Guid _project = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _agent = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _stranger = Guid.Parse("cccccccc-cccc-7ccc-8ccc-cccccccccccc");
    private static readonly Guid _trigger = Guid.Parse("22222222-2222-7222-8222-222222222222");
    private static readonly Guid _session = Guid.Parse("33333333-3333-7333-8333-333333333333");
    private static readonly Guid _turn = Guid.Parse("44444444-4444-7444-8444-444444444444");

    private const string _scheduleJson = """
        {
          "kind": "interval",
          "purpose": "check the build",
          "targets": ["aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa"],
          "spec": {"every": "PT30M", "anchor": "2026-11-08T12:00:00Z"}
        }
        """;

    /// <summary>In-memory directory: agents keyed by id, each with an active flag. Every agent is in <see cref="_project"/>.</summary>
    private sealed class InMemoryRyuseiAgentDirectory(params (Guid Agent, bool Active)[] agents) : IRyuseiAgentDirectory
    {
        private readonly Dictionary<Guid, bool> _agents = agents.ToDictionary(a => a.Agent, a => a.Active);

        public Task<RyuseiAgentLookup> ResolveAsync(Guid callerAgentId, IReadOnlyCollection<Guid> targetAgentIds, CancellationToken ct)
        {
            RyuseiActor? caller = _agents.ContainsKey(callerAgentId) ? RyuseiActor.ForAgent(callerAgentId, _project) : null;
            var targets = targetAgentIds
                .Where(_agents.ContainsKey)
                .Distinct()
                .Select(id => new RyuseiTargetInfo(id, _project, _agents[id]))
                .ToList();
            return Task.FromResult(new RyuseiAgentLookup(caller, targets));
        }
    }

    /// <summary>The service is never reached by these tests. Its data source points at a closed port, so a call would fail loudly.</summary>
    private static RyuseiTriggerService Service(CapturingAuditLog audit) =>
        new(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1"), audit);

    private static async Task<JsonNode> RunAsync(IContextualTool tool, string json, Guid callerAgentId, int chainDepth = 0)
    {
        using var doc = JsonDocument.Parse(json);
        var raw = await tool.ExecuteAsync(
            doc.RootElement.Clone(),
            new ToolCallContext(callerAgentId, _session, _turn, chainDepth),
            TestContext.Current.CancellationToken);
        return JsonNode.Parse(raw)!;
    }

    [Fact]
    public void The_registry_refuses_an_unknown_tool_name_and_holds_the_four_contract_names()
    {
        var audit = new CapturingAuditLog();
        var directory = new InMemoryRyuseiAgentDirectory((_agent, true));
        var service = Service(audit);
        var registry = new ToolRegistry(
        [
            (RyuseiToolContract.ScheduleToolName, new RyuseiScheduleTool(service, directory, TimeProvider.System)),
            (RyuseiToolContract.ListToolName, new RyuseiListTool(service, directory)),
            (RyuseiToolContract.PauseToolName, new RyuseiPauseTool(service, directory, TimeProvider.System)),
            (RyuseiToolContract.CancelToolName, new RyuseiCancelTool(service, directory, TimeProvider.System)),
        ]);

        Assert.False(registry.TryGet("ryusei.delete", out _));
        Assert.False(registry.TryGet("Ryusei.Schedule", out _));
        Assert.True(registry.TryGet(RyuseiToolContract.ScheduleToolName, out _));
        Assert.True(registry.TryGet(RyuseiToolContract.CancelToolName, out _));
        Assert.False(RyuseiToolContract.TryResolveTool("ryusei.delete", out _));
    }

    [Fact]
    public async Task A_schedule_from_a_turn_at_the_maximum_chain_depth_is_refused_as_too_deep()
    {
        // The new trigger would be one fire deeper than the caller, so the maximum refuses it. The refusal happens before any
        // database work, so the closed-port service is never reached.
        var audit = new CapturingAuditLog();
        var directory = new InMemoryRyuseiAgentDirectory((_agent, true));
        var tool = new RyuseiScheduleTool(Service(audit), directory, TimeProvider.System);

        var result = await RunAsync(tool, _scheduleJson, _agent, chainDepth: RyuseiDefaults.MaxChainDepth);

        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Equal("ChainDepthExceeded", result["refusal"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_schedule_call_from_a_caller_outside_the_directory_is_refused_before_the_service()
    {
        var audit = new CapturingAuditLog();
        var directory = new InMemoryRyuseiAgentDirectory((_agent, true));
        var tool = new RyuseiScheduleTool(Service(audit), directory, TimeProvider.System);

        var result = await RunAsync(tool, _scheduleJson, _stranger);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Equal("NotAuthorized", result["refusal"]!.GetValue<string>());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_schedule_call_that_names_an_unknown_target_is_refused_before_the_service()
    {
        var audit = new CapturingAuditLog();
        var directory = new InMemoryRyuseiAgentDirectory((_agent, true));
        var tool = new RyuseiScheduleTool(Service(audit), directory, TimeProvider.System);
        var json = _scheduleJson.Replace("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb", StringComparison.Ordinal);

        var result = await RunAsync(tool, json, _agent);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", result["status"]!.GetValue<string>());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_schedule_call_with_a_malformed_spec_is_invalid_arguments_before_the_directory()
    {
        var audit = new CapturingAuditLog();
        var directory = new InMemoryRyuseiAgentDirectory((_agent, true));
        var tool = new RyuseiScheduleTool(Service(audit), directory, TimeProvider.System);
        var json = _scheduleJson.Replace("\"kind\": \"interval\"", "\"kind\": \"nonsense\"", StringComparison.Ordinal);

        var result = await RunAsync(tool, json, _agent);

        Assert.Equal("invalid_arguments", result["status"]!.GetValue<string>());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_list_call_from_a_caller_outside_the_directory_is_refused()
    {
        var audit = new CapturingAuditLog();
        var tool = new RyuseiListTool(Service(audit), new InMemoryRyuseiAgentDirectory((_agent, true)));

        var result = await RunAsync(tool, "{}", _stranger);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_list_call_with_an_unknown_state_is_invalid_arguments()
    {
        var audit = new CapturingAuditLog();
        var tool = new RyuseiListTool(Service(audit), new InMemoryRyuseiAgentDirectory((_agent, true)));

        var result = await RunAsync(tool, """{"state":"deleted"}""", _agent);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", result["status"]!.GetValue<string>());
    }

    [Fact]
    public void A_list_result_has_the_documented_shape_and_leaves_the_spec_out()
    {
        var record = new RyuseiTriggerRecord(
            TriggerId: _trigger,
            ProjectId: _project,
            CreatedBy: _agent,
            Kind: RyuseiTriggerKind.Interval,
            TargetAgentIds: [_agent],
            Purpose: "check the build",
            SpecJson: """{"every":"PT30M"}""",
            TaskId: null,
            AttachGcId: null,
            PingMode: RyuseiPingMode.None,
            State: RyuseiTriggerState.Active,
            NextFireAt: new DateTimeOffset(2026, 11, 8, 12, 30, 0, TimeSpan.Zero),
            FireCount: 0,
            ConsecutiveRefusals: 0,
            MaxFires: null,
            ExpiresAt: null,
            ChainDepth: 0,
            Version: 3,
            CreatedAt: new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero),
            UpdatedAt: new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

        var result = RyuseiToolContract.BuildList([record]);

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal(1, result["count"]!.GetValue<int>());
        var item = result["triggers"]![0]!.AsObject();
        Assert.Equal(_trigger.ToString("D"), item["triggerId"]!.GetValue<string>());
        Assert.Equal("interval", item["kind"]!.GetValue<string>());
        Assert.Equal("active", item["state"]!.GetValue<string>());
        Assert.Equal("check the build", item["purpose"]!.GetValue<string>());
        Assert.Equal(_agent.ToString("D"), item["targets"]![0]!.GetValue<string>());
        Assert.Equal(3L, item["version"]!.GetValue<long>());
        Assert.False(item.ContainsKey("spec"));
        Assert.False(item.ContainsKey("SpecJson"));
    }

    [Fact]
    public async Task A_pause_with_an_invalid_trigger_id_is_invalid_arguments()
    {
        var audit = new CapturingAuditLog();
        var tool = new RyuseiPauseTool(Service(audit), new InMemoryRyuseiAgentDirectory((_agent, true)), TimeProvider.System);

        var result = await RunAsync(tool, """{"trigger_id":"not-a-guid"}""", _agent);

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", result["status"]!.GetValue<string>());
        Assert.Equal("trigger_id must be a GUID.", result["message"]!.GetValue<string>());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_cancel_with_no_trigger_id_is_invalid_arguments()
    {
        var audit = new CapturingAuditLog();
        var tool = new RyuseiCancelTool(Service(audit), new InMemoryRyuseiAgentDirectory((_agent, true)), TimeProvider.System);

        var result = await RunAsync(tool, "{}", _agent);

        Assert.Equal("invalid_arguments", result["status"]!.GetValue<string>());
        Assert.Equal("trigger_id is required.", result["message"]!.GetValue<string>());
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task A_pause_from_a_caller_outside_the_directory_is_refused()
    {
        var audit = new CapturingAuditLog();
        var tool = new RyuseiPauseTool(Service(audit), new InMemoryRyuseiAgentDirectory((_agent, true)), TimeProvider.System);

        var result = await RunAsync(tool, $$"""{"trigger_id":"{{_trigger:D}}"}""", _stranger);

        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Equal("NotAuthorized", result["refusal"]!.GetValue<string>());
        Assert.Empty(audit.Entries);
    }
}
