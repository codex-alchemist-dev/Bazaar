using Bazaar.Server.WorkLife;

namespace Bazaar.Server.Tests.WorkLife;

public class PresenceRulesTests
{
    private const string Working = "working";

    private static PresenceInput In(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        PresenceToolClass tool = PresenceToolClass.None,
        PresenceGcContext gc = PresenceGcContext.Other,
        bool throttled = false,
        bool covered = false,
        bool waitingForApproval = false) =>
        new(availability, activity, tool, gc, throttled, covered, waitingForApproval);

    private static PresenceResult Derive(PresenceInput input, string idleLabel = Working) =>
        PresenceRules.Derive(input, idleLabel);

    [Theory]
    [InlineData(PresenceLabel.OnVacation, "on vacation")]
    [InlineData(PresenceLabel.Sleeping, "sleeping")]
    [InlineData(PresenceLabel.InMeeting, "in meeting")]
    [InlineData(PresenceLabel.Researching, "researching")]
    [InlineData(PresenceLabel.Reviewing, "reviewing")]
    [InlineData(PresenceLabel.Thinking, "thinking")]
    [InlineData(PresenceLabel.Working, "working")]
    [InlineData(PresenceLabel.HelpingAnotherProject, "helping another project")]
    public void Each_base_label_has_its_text(PresenceLabel label, string text) =>
        Assert.Equal(text, PresenceRules.LabelText(label));

    [Fact]
    public void Default_idle_label_is_working() =>
        Assert.Equal("working", PresenceRules.DefaultIdleLabel);

    [Theory]
    [InlineData(PresenceActivityKind.Idle)]
    [InlineData(PresenceActivityKind.ModelCall)]
    [InlineData(PresenceActivityKind.Tool)]
    [InlineData(PresenceActivityKind.Waiting)]
    [InlineData(PresenceActivityKind.Drafting)]
    public void Vacation_is_on_vacation_whatever_the_activity(PresenceActivityKind activity)
    {
        var result = Derive(In(PresenceAvailability.Vacation, activity, PresenceToolClass.Research));

        Assert.Equal(new PresenceResult("on vacation", PresenceOverlay.None), result);
    }

    [Fact]
    public void Vacation_beats_off_and_a_research_tool_turn()
    {
        // The same activity under Off shows what the agent is doing; under vacation the vacation label wins.
        Assert.Equal("researching", Derive(In(PresenceAvailability.Off, PresenceActivityKind.Tool, PresenceToolClass.Research)).Label);
        Assert.Equal("on vacation", Derive(In(PresenceAvailability.Vacation, PresenceActivityKind.Tool, PresenceToolClass.Research)).Label);
    }

    [Fact]
    public void Vacation_beats_break_and_drops_the_on_break_overlay()
    {
        var result = Derive(In(PresenceAvailability.Vacation, PresenceActivityKind.Idle));

        Assert.Equal(new PresenceResult("on vacation", PresenceOverlay.None), result);
    }

    [Theory]
    [InlineData(PresenceAvailability.Off, PresenceOverlay.None)]
    [InlineData(PresenceAvailability.Break, PresenceOverlay.OnBreak)]
    public void Off_or_break_while_idle_is_sleeping(PresenceAvailability availability, PresenceOverlay expectedOverlays)
    {
        var result = Derive(In(availability, PresenceActivityKind.Idle));

        Assert.Equal(new PresenceResult("sleeping", expectedOverlays), result);
    }

    [Theory]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Waiting, PresenceOverlay.None)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Drafting, PresenceOverlay.None)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Waiting, PresenceOverlay.OnBreak)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Drafting, PresenceOverlay.OnBreak)]
    public void Off_or_break_while_waiting_or_drafting_is_sleeping(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        PresenceOverlay expectedOverlays)
    {
        var result = Derive(In(availability, activity));

        Assert.Equal(new PresenceResult("sleeping", expectedOverlays), result);
    }

    [Theory]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.Other, PresenceLabel.Thinking)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.Other, PresenceLabel.Researching)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Tool, PresenceToolClass.Review, PresenceGcContext.Other, PresenceLabel.Reviewing)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild, PresenceGcContext.Other, PresenceLabel.Working)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild, PresenceGcContext.CouncilOrVote, PresenceLabel.InMeeting)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Tool, PresenceToolClass.None, PresenceGcContext.OtherProjectGc, PresenceLabel.HelpingAnotherProject)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.Other, PresenceLabel.Thinking)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.Other, PresenceLabel.Researching)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Tool, PresenceToolClass.Review, PresenceGcContext.Other, PresenceLabel.Reviewing)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild, PresenceGcContext.ProjectGc, PresenceLabel.Working)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.CouncilOrVote, PresenceLabel.InMeeting)]
    public void Off_or_break_with_a_turn_uses_the_on_shift_rows(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        PresenceToolClass tool,
        PresenceGcContext gc,
        PresenceLabel expected)
    {
        var result = Derive(In(availability, activity, tool, gc));

        Assert.Equal(PresenceRules.LabelText(expected), result.Label);
    }

    [Fact]
    public void Break_with_a_turn_keeps_the_on_break_overlay()
    {
        var result = Derive(In(PresenceAvailability.Break, PresenceActivityKind.Tool, PresenceToolClass.Research));

        Assert.Equal(new PresenceResult("researching", PresenceOverlay.OnBreak), result);
    }

    [Theory]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.Other, PresenceLabel.Thinking)]
    [InlineData(PresenceAvailability.Draining, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.Other, PresenceLabel.Thinking)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.Other, PresenceLabel.Researching)]
    [InlineData(PresenceAvailability.Draining, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.Other, PresenceLabel.Researching)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.Review, PresenceGcContext.Other, PresenceLabel.Reviewing)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild, PresenceGcContext.Other, PresenceLabel.Working)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.None, PresenceGcContext.Other, PresenceLabel.Working)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.CouncilOrVote, PresenceLabel.InMeeting)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.CouncilOrVote, PresenceLabel.InMeeting)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild, PresenceGcContext.CouncilOrVote, PresenceLabel.InMeeting)]
    // Listed order: the tool-class and model-call rows come before the other-project GC row (see PresenceRules remarks).
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild, PresenceGcContext.OtherProjectGc, PresenceLabel.Working)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.None, PresenceGcContext.OtherProjectGc, PresenceLabel.HelpingAnotherProject)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.OtherProjectGc, PresenceLabel.Researching)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.OtherProjectGc, PresenceLabel.Thinking)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.Research, PresenceGcContext.ProjectGc, PresenceLabel.Researching)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.ModelCall, PresenceToolClass.None, PresenceGcContext.ProjectGc, PresenceLabel.Thinking)]
    public void On_shift_and_draining_turn_rows(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        PresenceToolClass tool,
        PresenceGcContext gc,
        PresenceLabel expected)
    {
        var result = Derive(In(availability, activity, tool, gc));

        Assert.Equal(PresenceRules.LabelText(expected), result.Label);
    }

    [Theory]
    [InlineData(PresenceActivityKind.Idle)]
    [InlineData(PresenceActivityKind.Drafting)]
    [InlineData(PresenceActivityKind.Waiting)]
    public void On_shift_idle_drafting_and_waiting_use_the_idle_label(PresenceActivityKind activity)
    {
        var result = Derive(In(PresenceAvailability.OnShift, activity), "heads down");

        Assert.Equal("heads down", result.Label);
    }

    [Theory]
    [InlineData(PresenceAvailability.OnShift)]
    [InlineData(PresenceAvailability.Draining)]
    public void Waiting_uses_the_idle_rule_while_on_shift_or_draining(PresenceAvailability availability)
    {
        var result = Derive(In(availability, PresenceActivityKind.Waiting));

        Assert.Equal(new PresenceResult(Working, PresenceOverlay.Idle), result);
    }

    [Theory]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Idle, PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Drafting, PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.Draining, PresenceActivityKind.Idle, PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceOverlay.None)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Idle, PresenceOverlay.None)]
    public void Idle_overlay_is_added_only_on_the_idle_rule_when_the_label_is_working(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        PresenceOverlay expected)
    {
        var result = Derive(In(availability, activity, PresenceToolClass.EditOrBuild));

        Assert.Equal(expected, result.Overlays & PresenceOverlay.Idle);
    }

    [Theory]
    [InlineData("available")]
    [InlineData("heads down")]
    public void Idle_overlay_is_not_added_when_the_idle_label_is_custom(string idleLabel)
    {
        var result = Derive(In(PresenceAvailability.OnShift, PresenceActivityKind.Idle), idleLabel);

        Assert.Equal(new PresenceResult(idleLabel, PresenceOverlay.None), result);
    }

    [Theory]
    [InlineData("available")]
    [InlineData("heads down")]
    [InlineData("on call")]
    public void Custom_idle_label_text_is_used_verbatim(string idleLabel)
    {
        var idle = Derive(In(PresenceAvailability.Draining, PresenceActivityKind.Drafting), idleLabel);
        var turn = Derive(In(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.EditOrBuild), idleLabel);

        Assert.Equal(idleLabel, idle.Label);
        Assert.Equal("working", turn.Label);
    }

    [Theory]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Waiting, true, PresenceOverlay.WaitingForApproval | PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Waiting, false, PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Idle, true, PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.Draining, PresenceActivityKind.Waiting, true, PresenceOverlay.WaitingForApproval | PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Waiting, true, PresenceOverlay.WaitingForApproval)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Waiting, true, PresenceOverlay.WaitingForApproval | PresenceOverlay.OnBreak)]
    public void Waiting_for_approval_overlay_needs_a_waiting_turn_and_a_pending_approval(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        bool waitingForApproval,
        PresenceOverlay expected)
    {
        var result = Derive(In(availability, activity, waitingForApproval: waitingForApproval));

        Assert.Equal(expected, result.Overlays);
    }

    [Fact]
    public void Waiting_for_approval_is_not_shown_without_waiting_activity()
    {
        var result = Derive(In(PresenceAvailability.OnShift, PresenceActivityKind.Tool, PresenceToolClass.Research, waitingForApproval: true));

        Assert.Equal(new PresenceResult("researching", PresenceOverlay.None), result);
    }

    [Theory]
    [InlineData(PresenceAvailability.Vacation, PresenceOverlay.Throttled | PresenceOverlay.Covered)]
    [InlineData(PresenceAvailability.OnShift, PresenceOverlay.Throttled | PresenceOverlay.Covered)]
    [InlineData(PresenceAvailability.Draining, PresenceOverlay.Throttled | PresenceOverlay.Covered)]
    [InlineData(PresenceAvailability.Off, PresenceOverlay.Throttled | PresenceOverlay.Covered)]
    [InlineData(PresenceAvailability.Break, PresenceOverlay.Throttled | PresenceOverlay.Covered | PresenceOverlay.OnBreak)]
    public void Throttled_and_covered_show_in_every_state(PresenceAvailability availability, PresenceOverlay expected)
    {
        var result = Derive(In(availability, PresenceActivityKind.Tool, PresenceToolClass.Research, throttled: true, covered: true));

        Assert.Equal(expected, result.Overlays);
    }

    [Theory]
    [InlineData(PresenceAvailability.OnShift, PresenceOverlay.Throttled)]
    [InlineData(PresenceAvailability.Off, PresenceOverlay.Throttled)]
    public void Throttled_alone_sets_only_throttled(PresenceAvailability availability, PresenceOverlay expected)
    {
        var result = Derive(In(availability, PresenceActivityKind.ModelCall, throttled: true));

        Assert.Equal(expected, result.Overlays);
    }

    [Theory]
    [InlineData(PresenceAvailability.OnShift, PresenceOverlay.Covered)]
    [InlineData(PresenceAvailability.Off, PresenceOverlay.Covered)]
    public void Covered_alone_sets_only_covered(PresenceAvailability availability, PresenceOverlay expected)
    {
        var result = Derive(In(availability, PresenceActivityKind.ModelCall, covered: true));

        Assert.Equal(expected, result.Overlays);
    }

    [Theory]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Idle, PresenceOverlay.OnBreak)]
    [InlineData(PresenceAvailability.Break, PresenceActivityKind.Tool, PresenceOverlay.OnBreak)]
    [InlineData(PresenceAvailability.OnShift, PresenceActivityKind.Idle, PresenceOverlay.Idle)]
    [InlineData(PresenceAvailability.Draining, PresenceActivityKind.Tool, PresenceOverlay.None)]
    [InlineData(PresenceAvailability.Off, PresenceActivityKind.Idle, PresenceOverlay.None)]
    public void On_break_overlay_is_set_only_during_break(
        PresenceAvailability availability,
        PresenceActivityKind activity,
        PresenceOverlay expected)
    {
        var result = Derive(In(availability, activity, PresenceToolClass.Research));

        Assert.Equal(expected, result.Overlays);
    }

    [Fact]
    public void Ward_overlay_is_never_derived_here()
    {
        foreach (var availability in Enum.GetValues<PresenceAvailability>())
            foreach (var activity in Enum.GetValues<PresenceActivityKind>())
                foreach (var tool in Enum.GetValues<PresenceToolClass>())
                    foreach (var gc in Enum.GetValues<PresenceGcContext>())
                    {
                        var result = Derive(In(availability, activity, tool, gc, throttled: true, covered: true, waitingForApproval: true));

                        Assert.False(result.Overlays.HasFlag(PresenceOverlay.Ward));
                    }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void Empty_or_whitespace_idle_label_is_rejected(string? idleLabel)
    {
        var input = In(PresenceAvailability.OnShift, PresenceActivityKind.Idle);

        Assert.ThrowsAny<ArgumentException>(() => PresenceRules.Derive(input, idleLabel!));
    }

    [Fact]
    public void Idle_label_is_validated_even_when_the_idle_label_is_not_used()
    {
        var input = In(PresenceAvailability.Vacation, PresenceActivityKind.Idle);

        Assert.ThrowsAny<ArgumentException>(() => PresenceRules.Derive(input, " "));
    }
}
