using System.Text.Json;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>The tool-step rules across a crash, the text the model reads, and the request hash. Pure: no database.</summary>
public sealed class ToolStepPlannerTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static TurnStepRecord Step(TurnStepStatus status, bool idempotent) =>
        new(Guid.CreateVersion7(), 1, TurnStepKind.Tool, status, "sha256:x", null, idempotent, DateTimeOffset.UnixEpoch, null);

    [Fact]
    public void A_call_with_no_earlier_step_is_dispatched()
    {
        Assert.Equal(ToolStepAction.Dispatch, ToolStepPlanner.Plan(null, idempotentNow: false));
        Assert.Equal(ToolStepAction.Dispatch, ToolStepPlanner.Plan(null, idempotentNow: true));
    }

    [Fact]
    public void A_completed_step_is_reused_whatever_the_idempotency_settings_say()
    {
        Assert.Equal(ToolStepAction.Reuse, ToolStepPlanner.Plan(Step(TurnStepStatus.Completed, idempotent: false), idempotentNow: false));
        Assert.Equal(ToolStepAction.Reuse, ToolStepPlanner.Plan(Step(TurnStepStatus.Completed, idempotent: true), idempotentNow: false));
    }

    [Fact]
    public void An_unknown_step_is_never_run_again_even_when_it_was_idempotent()
    {
        Assert.Equal(ToolStepAction.Unknown, ToolStepPlanner.Plan(Step(TurnStepStatus.Unknown, idempotent: false), idempotentNow: true));
        Assert.Equal(ToolStepAction.Unknown, ToolStepPlanner.Plan(Step(TurnStepStatus.Unknown, idempotent: true), idempotentNow: true));
    }

    [Fact]
    public void An_abandoned_idempotent_step_is_dispatched_again_only_while_the_tool_is_still_idempotent()
    {
        Assert.Equal(ToolStepAction.Dispatch, ToolStepPlanner.Plan(Step(TurnStepStatus.Abandoned, idempotent: true), idempotentNow: true));
        Assert.Equal(ToolStepAction.Unknown, ToolStepPlanner.Plan(Step(TurnStepStatus.Abandoned, idempotent: true), idempotentNow: false));
    }

    [Fact]
    public void An_abandoned_non_idempotent_step_is_unknown()
    {
        Assert.Equal(ToolStepAction.Unknown, ToolStepPlanner.Plan(Step(TurnStepStatus.Abandoned, idempotent: false), idempotentNow: true));
    }

    [Fact]
    public void A_pending_step_left_by_recovery_follows_the_same_rule_as_an_abandoned_one()
    {
        Assert.Equal(ToolStepAction.Dispatch, ToolStepPlanner.Plan(Step(TurnStepStatus.Pending, idempotent: true), idempotentNow: true));
        Assert.Equal(ToolStepAction.Unknown, ToolStepPlanner.Plan(Step(TurnStepStatus.Pending, idempotent: false), idempotentNow: true));
    }

    [Fact]
    public void A_model_step_and_an_idempotent_tool_step_are_abandoned_when_unfinished_and_any_other_tool_step_is_unknown()
    {
        Assert.Equal(TurnStepStatus.Abandoned, ToolStepPlanner.UnfinishedStatus(TurnStepKind.Model, idempotent: false));
        Assert.Equal(TurnStepStatus.Abandoned, ToolStepPlanner.UnfinishedStatus(TurnStepKind.Tool, idempotent: true));
        Assert.Equal(TurnStepStatus.Unknown, ToolStepPlanner.UnfinishedStatus(TurnStepKind.Tool, idempotent: false));
    }

    [Fact]
    public void An_executed_result_reaches_the_model_as_its_json()
    {
        Assert.Equal("""{"notes":"hi"}""", ToolStepPlanner.ResultText(ToolDispatchOutcome.Executed, """{"notes":"hi"}""", null));
        Assert.Equal("null", ToolStepPlanner.ResultText(ToolDispatchOutcome.Executed, null, null));
    }

    [Fact]
    public void A_denied_call_tells_the_model_it_did_not_run_and_gives_the_reason()
    {
        var text = ToolStepPlanner.ResultText(ToolDispatchOutcome.Denied, null, "RequiresGrant");

        Assert.Contains("did not run", text);
        Assert.Contains("RequiresGrant", text);
        Assert.Equal("The tool call was denied by policy and did not run.", ToolStepPlanner.ResultText(ToolDispatchOutcome.Denied, null, null));
    }

    [Fact]
    public void A_call_that_needs_approval_tells_the_model_it_did_not_run()
    {
        Assert.Equal(
            "The tool call needs owner approval and did not run.",
            ToolStepPlanner.ResultText(ToolDispatchOutcome.PendingApproval, null, "RequireApproval"));
    }

    [Fact]
    public void An_unknown_outcome_gives_the_unknown_text_that_says_not_to_assume_either_way()
    {
        var text = ToolStepPlanner.ResultText(ToolDispatchOutcome.Unknown, null, null);

        Assert.Equal(ToolStepPlanner.UnknownResult, text);
        Assert.Contains("not idempotent", text);
        Assert.Contains("do not assume", text);
    }

    [Fact]
    public void The_request_hash_changes_with_the_model_request_and_with_each_part_of_the_call()
    {
        var call = new ModelStepToolCall("call-1", "read_file", """{"path":"a"}""");
        var same = ToolStepPlanner.Hash("sha256:one", call);

        Assert.Equal(same, ToolStepPlanner.Hash("sha256:one", call));
        Assert.NotEqual(same, ToolStepPlanner.Hash("sha256:two", call));
        Assert.NotEqual(same, ToolStepPlanner.Hash("sha256:one", call with { CallId = "call-2" }));
        Assert.NotEqual(same, ToolStepPlanner.Hash("sha256:one", call with { Name = "write_file" }));
        Assert.NotEqual(same, ToolStepPlanner.Hash("sha256:one", call with { ArgumentsJson = """{"path":"b"}""" }));
    }

    [Fact]
    public void A_stored_tool_step_round_trips_with_its_outcome_and_result()
    {
        var stored = new ToolStepResponse("call-1", "read_file", ToolDispatchOutcome.Executed, """{"notes":"hi"}""", null);

        var json = JsonSerializer.Serialize(stored, _json);
        var back = JsonSerializer.Deserialize<ToolStepResponse>(json, _json);

        Assert.Equal(stored, back);
        Assert.Contains("\"callId\":\"call-1\"", json);
    }
}
