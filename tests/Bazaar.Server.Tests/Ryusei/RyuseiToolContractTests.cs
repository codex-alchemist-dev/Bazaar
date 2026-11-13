using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.RyuseiFuda.Orrery;

namespace Bazaar.Server.Tests.Ryusei;

/// <summary>
/// Argument parsing and result shaping for the four RyuseiFuda agent tools (doc 38, WP-R9). Pure: no database, no clock.
/// </summary>
public sealed class RyuseiToolContractTests
{
    private static readonly Guid _agent = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _gc = Guid.Parse("11111111-1111-7111-8111-111111111111");
    private static readonly Guid _trigger = Guid.Parse("22222222-2222-7222-8222-222222222222");

    private static JsonElement Args(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string ScheduleJson(string extra = "", string kind = "\"interval\"", string spec = "{\"every\":\"PT30M\",\"anchor\":\"2026-11-08T12:00:00Z\"}") =>
        $$"""
        {
          "kind": {{kind}},
          "purpose": "check the build",
          "targets": ["{{_agent:D}}"],
          "spec": {{spec}}{{extra}}
        }
        """;

    // ryusei.schedule

    [Fact]
    public void Valid_interval_schedule_parses_with_its_spec_and_attached_gc()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args(ScheduleJson(
            extra: $", \"attach_gc_id\": \"{_gc:D}\", \"ping_mode\": \"attach\"")));

        Assert.True(parse.IsValid, parse.Refusal);
        var request = parse.Request!;
        Assert.Equal(RyuseiTriggerKind.Interval, request.Kind);
        var spec = Assert.IsType<RyuseiIntervalTriggerSpec>(request.Spec);
        Assert.Equal(TimeSpan.FromMinutes(30), spec.Every);
        Assert.Equal("check the build", request.Purpose);
        Assert.Single(request.TargetAgentIds);
        Assert.Equal(_agent, request.TargetAgentIds[0]);
        Assert.Equal(RyuseiPingMode.Attach, request.PingMode);
        Assert.Equal(_gc, request.AttachGcId);
    }

    [Fact]
    public void Timer_schedule_without_gc_defaults_to_no_ping()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args(ScheduleJson(
            kind: "\"timer\"", spec: "{\"fireAt\":\"2026-11-08T13:00:00Z\"}")));

        Assert.True(parse.IsValid, parse.Refusal);
        Assert.IsType<RyuseiTimerTriggerSpec>(parse.Request!.Spec);
        Assert.Equal(RyuseiPingMode.None, parse.Request.PingMode);
        Assert.Null(parse.Request.AttachGcId);
    }

    [Fact]
    public void Attach_gc_without_ping_mode_defaults_to_attach()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args(ScheduleJson(
            extra: $", \"attach_gc_id\": \"{_gc:D}\"")));

        Assert.True(parse.IsValid, parse.Refusal);
        Assert.Equal(RyuseiPingMode.Attach, parse.Request!.PingMode);
    }

    [Fact]
    public void Missing_kind_is_refused_with_a_reason_naming_it()
    {
        var json = """{"purpose":"x","targets":["AGENT"],"spec":{}}""".Replace("AGENT", _agent.ToString("D"));

        var parse = RyuseiToolContract.ParseSchedule(Args(json));

        Assert.False(parse.IsValid);
        Assert.Equal("kind is required.", parse.Refusal);
    }

    [Fact]
    public void Missing_purpose_is_refused_with_a_reason_naming_it()
    {
        var json = """{"kind":"timer","targets":["AGENT"],"spec":{"fireAt":"2026-11-08T13:00:00Z"}}""".Replace("AGENT", _agent.ToString("D"));

        var parse = RyuseiToolContract.ParseSchedule(Args(json));

        Assert.False(parse.IsValid);
        Assert.Equal("purpose is required.", parse.Refusal);
    }

    [Fact]
    public void Missing_targets_is_refused()
    {
        var json = """{"kind":"timer","purpose":"x","spec":{"fireAt":"2026-11-08T13:00:00Z"}}""";

        var parse = RyuseiToolContract.ParseSchedule(Args(json));

        Assert.False(parse.IsValid);
        Assert.Contains("targets", parse.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_targets_is_refused_at_shape_level()
    {
        var json = """{"kind":"timer","purpose":"x","targets":[],"spec":{"fireAt":"2026-11-08T13:00:00Z"}}""";

        var parse = RyuseiToolContract.ParseSchedule(Args(json));

        Assert.False(parse.IsValid);
        Assert.Equal("targets must name at least one agent.", parse.Refusal);
    }

    [Fact]
    public void Target_that_is_not_a_guid_is_refused()
    {
        var json = """{"kind":"timer","purpose":"x","targets":["not-a-guid"],"spec":{"fireAt":"2026-11-08T13:00:00Z"}}""";

        var parse = RyuseiToolContract.ParseSchedule(Args(json));

        Assert.False(parse.IsValid);
        Assert.Equal("Each target must be an agent GUID.", parse.Refusal);
    }

    [Fact]
    public void Spec_missing_a_field_for_its_kind_is_refused_naming_the_field()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args(ScheduleJson(
            kind: "\"timer\"", spec: "{}")));

        Assert.False(parse.IsValid);
        Assert.Contains("fireAt", parse.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void Spec_that_is_not_an_object_is_refused()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args(ScheduleJson(
            kind: "\"timer\"", spec: "\"2026-11-08\"")));

        Assert.False(parse.IsValid);
        Assert.Equal("spec is required and must be a JSON object.", parse.Refusal);
    }

    [Fact]
    public void Unknown_kind_is_refused()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args(ScheduleJson(kind: "\"cron\"")));

        Assert.False(parse.IsValid);
        Assert.Equal("kind must be one of timer, interval, watcher, event.", parse.Refusal);
    }

    [Fact]
    public void Unknown_ping_mode_is_refused()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args(ScheduleJson(extra: ", \"ping_mode\": \"shout\"")));

        Assert.False(parse.IsValid);
        Assert.Equal("ping_mode must be one of none, attach, new_gc.", parse.Refusal);
    }

    [Fact]
    public void Blank_purpose_is_refused()
    {
        var json = """{"kind":"timer","purpose":"   ","targets":["AGENT"],"spec":{"fireAt":"2026-11-08T13:00:00Z"}}""".Replace("AGENT", _agent.ToString("D"));

        var parse = RyuseiToolContract.ParseSchedule(Args(json));

        Assert.False(parse.IsValid);
        Assert.Equal("purpose must not be blank.", parse.Refusal);
    }

    [Fact]
    public void Arguments_that_are_not_an_object_are_refused()
    {
        var parse = RyuseiToolContract.ParseSchedule(Args("[1,2]"));

        Assert.False(parse.IsValid);
        Assert.Equal("The arguments must be a JSON object.", parse.Refusal);
    }

    // Tool names

    [Theory]
    [InlineData("ryusei.schedule", RyuseiToolName.Schedule)]
    [InlineData("ryusei.list", RyuseiToolName.List)]
    [InlineData("ryusei.pause", RyuseiToolName.Pause)]
    [InlineData("ryusei.cancel", RyuseiToolName.Cancel)]
    public void Each_registered_name_resolves_to_its_tool(string name, RyuseiToolName expected)
    {
        Assert.True(RyuseiToolContract.TryResolveTool(name, out var tool));
        Assert.Equal(expected, tool);
    }

    [Theory]
    [InlineData("ryusei.resume")]
    [InlineData("RYUSEI.LIST")]
    [InlineData("ryusei.list ")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_tool_name_is_refused(string? name)
    {
        Assert.False(RyuseiToolContract.TryResolveTool(name, out _));
    }

    // ryusei.list

    [Fact]
    public void Empty_list_arguments_use_the_defaults()
    {
        var parse = RyuseiToolContract.ParseList(Args("{}"));

        Assert.True(parse.IsValid, parse.Refusal);
        Assert.Null(parse.Request!.State);
        Assert.Equal(RyuseiToolContract.DefaultListLimit, parse.Request.Limit);
    }

    [Fact]
    public void List_with_state_and_limit_parses()
    {
        var parse = RyuseiToolContract.ParseList(Args("""{"state":"paused","limit":5}"""));

        Assert.True(parse.IsValid, parse.Refusal);
        Assert.Equal(RyuseiTriggerState.Paused, parse.Request!.State);
        Assert.Equal(5, parse.Request.Limit);
    }

    [Theory]
    [InlineData("""{"limit":0}""")]
    [InlineData("""{"limit":101}""")]
    public void List_limit_outside_the_range_is_refused(string json)
    {
        var parse = RyuseiToolContract.ParseList(Args(json));

        Assert.False(parse.IsValid);
        Assert.Equal("limit must be between 1 and 100.", parse.Refusal);
    }

    [Fact]
    public void List_limit_that_is_not_a_whole_number_is_refused()
    {
        var parse = RyuseiToolContract.ParseList(Args("""{"limit":"ten"}"""));

        Assert.False(parse.IsValid);
        Assert.Equal("limit must be a whole number.", parse.Refusal);
    }

    [Fact]
    public void List_unknown_state_is_refused()
    {
        var parse = RyuseiToolContract.ParseList(Args("""{"state":"skipped"}"""));

        Assert.False(parse.IsValid);
        Assert.Equal("state must be one of active, paused, fired, expired, cancelled.", parse.Refusal);
    }

    [Fact]
    public void BuildList_shapes_each_trigger_and_leaves_the_spec_out()
    {
        var record = new RyuseiTriggerRecord(
            TriggerId: _trigger,
            ProjectId: _gc,
            CreatedBy: _agent,
            Kind: RyuseiTriggerKind.Timer,
            TargetAgentIds: [_agent],
            Purpose: "check the build",
            SpecJson: "{\"fireAt\":\"2026-11-08T13:00:00Z\"}",
            TaskId: null,
            AttachGcId: null,
            PingMode: RyuseiPingMode.None,
            State: RyuseiTriggerState.Active,
            NextFireAt: new DateTimeOffset(2026, 11, 8, 13, 0, 0, TimeSpan.Zero),
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
        var item = result["triggers"]!.AsArray()[0]!.AsObject();
        Assert.Equal(_trigger.ToString("D"), item["triggerId"]!.GetValue<string>());
        Assert.Equal("timer", item["kind"]!.GetValue<string>());
        Assert.Equal("active", item["state"]!.GetValue<string>());
        Assert.Equal("check the build", item["purpose"]!.GetValue<string>());
        Assert.Equal(_agent.ToString("D"), item["targets"]!.AsArray()[0]!.GetValue<string>());
        Assert.Equal("2026-11-08T13:00:00.0000000+00:00", item["nextFireAt"]!.GetValue<string>());
        Assert.Equal(3L, item["version"]!.GetValue<long>());
        Assert.Null(item["spec"]);
    }

    [Fact]
    public void BuildList_with_no_rows_returns_an_empty_array()
    {
        var result = RyuseiToolContract.BuildList([]);

        Assert.Equal(0, result["count"]!.GetValue<int>());
        Assert.Empty(result["triggers"]!.AsArray());
    }

    // ryusei.pause and ryusei.cancel

    [Fact]
    public void Trigger_action_parses_its_trigger_id()
    {
        var parse = RyuseiToolContract.ParseTriggerAction(Args($$"""{"trigger_id":"{{_trigger:D}}"}"""));

        Assert.True(parse.IsValid, parse.Refusal);
        Assert.Equal(_trigger, parse.Request!.TriggerId);
    }

    [Fact]
    public void Trigger_action_without_an_id_is_refused()
    {
        var parse = RyuseiToolContract.ParseTriggerAction(Args("{}"));

        Assert.False(parse.IsValid);
        Assert.Equal("trigger_id is required.", parse.Refusal);
    }

    [Fact]
    public void Trigger_action_with_a_bad_id_is_refused()
    {
        var parse = RyuseiToolContract.ParseTriggerAction(Args("""{"trigger_id":"nope"}"""));

        Assert.False(parse.IsValid);
        Assert.Equal("trigger_id must be a GUID.", parse.Refusal);
    }

    // Result shaping

    [Fact]
    public void Ok_result_carries_the_trigger_id_and_version()
    {
        var result = RyuseiToolContract.BuildResult(RyuseiTriggerResult.Ok(_trigger, 4));

        Assert.True(result["ok"]!.GetValue<bool>());
        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.Equal(_trigger.ToString("D"), result["triggerId"]!.GetValue<string>());
        Assert.Equal(4L, result["version"]!.GetValue<long>());
        Assert.Null(result["refusal"]);
    }

    [Fact]
    public void Refused_result_carries_the_refusal_and_message()
    {
        var result = RyuseiToolContract.BuildResult(RyuseiTriggerResult.Refused(
            RyuseiRefusal.NotAuthorized, "Only the creator or the owner may cancel this trigger."));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("refused", result["status"]!.GetValue<string>());
        Assert.Equal("NotAuthorized", result["refusal"]!.GetValue<string>());
        Assert.Equal("Only the creator or the owner may cancel this trigger.", result["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(RyuseiTriggerStatus.NotFound, "not_found")]
    [InlineData(RyuseiTriggerStatus.InvalidState, "invalid_state")]
    [InlineData(RyuseiTriggerStatus.Conflict, "conflict")]
    public void Other_statuses_map_to_snake_case_text(RyuseiTriggerStatus status, string expected)
    {
        var result = RyuseiToolContract.BuildResult(RyuseiTriggerResult.Other(status, "message"));

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal(expected, result["status"]!.GetValue<string>());
    }

    [Fact]
    public void Refusal_for_bad_arguments_uses_a_fixed_status()
    {
        var result = RyuseiToolContract.BuildRefusal("kind is required.");

        Assert.False(result["ok"]!.GetValue<bool>());
        Assert.Equal("invalid_arguments", result["status"]!.GetValue<string>());
        Assert.Equal("kind is required.", result["message"]!.GetValue<string>());
    }
}
