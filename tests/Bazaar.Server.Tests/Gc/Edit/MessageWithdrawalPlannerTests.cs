using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Edit;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Edit;

/// <summary>Pure policy for the import-withdrawal version (docs/design/01 Edits, doc 44 8.8, PROPOSED).</summary>
public class MessageWithdrawalPlannerTests
{
    private static readonly Guid _gc = Guid.CreateVersion7();
    private static readonly Guid _message = Guid.CreateVersion7();
    private static readonly DateTimeOffset _now = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_withdrawal_is_a_system_edit_copying_the_text_with_the_job_id()
    {
        var result = MessageEditPlanner.PlanWithdrawal(new GcRecord(_gc, null, "general", null), _message, 3, "imported text", "job-7", _now);

        var plan = Assert.IsType<MessageEditAccepted>(result).Plan;
        Assert.Equal(4, plan.NewVersion.Version);
        Assert.Equal("imported text", plan.NewVersion.Body);
        Assert.Equal("job-7", plan.NewVersion.WithdrawnByImport);
        Assert.Equal(GcEventKinds.MessageEdit, plan.EditEvent.Kind);
        Assert.Equal(AuthorKind.System, plan.EditEvent.AuthorKind);
        Assert.Equal(4, plan.EditEvent.Version);
        Assert.Null(plan.SystemNote);
    }

    [Fact]
    public void The_author_only_rule_does_not_apply_and_an_unchanged_body_is_fine()
    {
        var result = MessageEditPlanner.PlanWithdrawal(new GcRecord(_gc, null, "general", null), _message, 1, "x", "job-1", _now);

        Assert.IsType<MessageEditAccepted>(result);
    }

    [Fact]
    public void An_archived_gc_still_refuses()
    {
        var result = MessageEditPlanner.PlanWithdrawal(new GcRecord(_gc, null, "general", _now), _message, 1, "x", "job-1", _now);

        Assert.Equal(MessageEditRefusal.ArchivedGc, Assert.IsType<MessageEditRefused>(result).Reason);
    }
}
