using Bazaar.Server.Kanban;

namespace Bazaar.Server.Tests.Kanban;

/// <summary>The transition table as pure code (doc 12, Transition table). No database needed.</summary>
public sealed class TaskTransitionRulesTests
{
    private static readonly TaskState[] _states = Enum.GetValues<TaskState>();

    private static TransitionContext Permissive(bool owner = true, bool agent = false) => new()
    {
        IsSystem = true,
        IsOwner = owner,
        IsAgent = agent,
        IsAssignee = agent,
        IsReviewer = true,
        HasReason = true,
        ReviewLinks = 1,
        MinReviewLinks = 1,
        ReviewerUngatedDone = true,
        Gate = TaskGate.None,
        GateSatisfied = false,
        HasPrLink = false,
        PrMerged = false,
    };

    [Theory]
    [InlineData(TaskState.Backlog, TaskState.Ready)]
    [InlineData(TaskState.Ready, TaskState.InProgress)]
    [InlineData(TaskState.InProgress, TaskState.Review)]
    [InlineData(TaskState.Review, TaskState.InProgress)]
    [InlineData(TaskState.Review, TaskState.Done)]
    [InlineData(TaskState.Done, TaskState.Review)]
    [InlineData(TaskState.Ready, TaskState.Backlog)]
    [InlineData(TaskState.InProgress, TaskState.Backlog)]
    [InlineData(TaskState.Review, TaskState.Backlog)]
    public void Exactly_the_nine_listed_moves_are_listed(TaskState from, TaskState to)
    {
        Assert.True(TaskTransitionRules.IsListed(from, to));
    }

    [Fact]
    public void Every_other_pair_is_not_a_transition()
    {
        var listed = _states.SelectMany(f => _states.Select(t => (f, t))).Count(p => TaskTransitionRules.IsListed(p.f, p.t));
        Assert.Equal(9, listed);

        foreach (var from in _states)
        {
            foreach (var to in _states.Where(to => !TaskTransitionRules.IsListed(from, to)))
            {
                Assert.Equal(TransitionOutcome.NotATransition, TaskTransitionRules.Evaluate(from, to, Permissive()));
            }
        }
    }

    [Fact]
    public void A_permissive_owner_context_allows_every_listed_move()
    {
        foreach (var from in _states)
        {
            foreach (var to in _states.Where(to => TaskTransitionRules.IsListed(from, to)))
            {
                var ctx = Permissive() with { IsAssignee = true };
                Assert.Equal(TransitionOutcome.Allowed, TaskTransitionRules.Evaluate(from, to, ctx));
            }
        }
    }

    [Fact]
    public void Review_to_done_on_a_pr_card_needs_the_merge()
    {
        var ctx = Permissive() with { HasPrLink = true, PrMerged = false };
        Assert.Equal(TransitionOutcome.PrNotMerged, TaskTransitionRules.Evaluate(TaskState.Review, TaskState.Done, ctx));
        Assert.Equal(TransitionOutcome.Allowed, TaskTransitionRules.Evaluate(TaskState.Review, TaskState.Done, ctx with { PrMerged = true }));
    }

    [Fact]
    public void Gated_tasks_need_their_gate_satisfied_before_done()
    {
        var gated = Permissive() with { Gate = TaskGate.Council, GateSatisfied = false };
        Assert.Equal(TransitionOutcome.GateNotSatisfied, TaskTransitionRules.Evaluate(TaskState.Review, TaskState.Done, gated));
        Assert.Equal(TransitionOutcome.Allowed, TaskTransitionRules.Evaluate(TaskState.Review, TaskState.Done, gated with { GateSatisfied = true }));
    }

    [Fact]
    public void Starting_needs_an_agent_or_owner_and_leaves_the_blocker_guard_to_the_service()
    {
        var stranger = Permissive(owner: false, agent: false) with { IsSystem = false };
        Assert.Equal(TransitionOutcome.NotAuthorised, TaskTransitionRules.Evaluate(TaskState.Ready, TaskState.InProgress, stranger));
        Assert.Equal(TransitionOutcome.Allowed, TaskTransitionRules.Evaluate(TaskState.Ready, TaskState.InProgress, stranger with { IsAgent = true }));
    }

    [Fact]
    public void Review_needs_an_assignee_and_enough_links()
    {
        var notAssignee = Permissive() with { IsAssignee = false };
        Assert.Equal(TransitionOutcome.NotAuthorised, TaskTransitionRules.Evaluate(TaskState.InProgress, TaskState.Review, notAssignee));
        Assert.Equal(TransitionOutcome.NeedsLinks, TaskTransitionRules.Evaluate(TaskState.InProgress, TaskState.Review, Permissive(agent: true) with { ReviewLinks = 0 }));
    }

    [Fact]
    public void Backward_moves_need_a_reason()
    {
        var noReason = Permissive() with { HasReason = false };
        Assert.Equal(TransitionOutcome.MissingReason, TaskTransitionRules.Evaluate(TaskState.Review, TaskState.InProgress, noReason));
        Assert.Equal(TransitionOutcome.MissingReason, TaskTransitionRules.Evaluate(TaskState.Done, TaskState.Review, noReason));
        Assert.Equal(TransitionOutcome.MissingReason, TaskTransitionRules.Evaluate(TaskState.Ready, TaskState.Backlog, noReason));
    }

    [Fact]
    public void Reopening_done_is_owner_only()
    {
        var reviewer = Permissive(owner: false) with { IsSystem = false, IsReviewer = true };
        Assert.Equal(TransitionOutcome.NotAuthorised, TaskTransitionRules.Evaluate(TaskState.Done, TaskState.Review, reviewer));
        Assert.Equal(TransitionOutcome.Allowed, TaskTransitionRules.Evaluate(TaskState.Done, TaskState.Review, Permissive()));
    }
}
