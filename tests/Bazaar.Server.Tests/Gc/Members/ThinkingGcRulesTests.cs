using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Members;

public class ThinkingGcRulesTests
{
    private static readonly Guid WorkingId = Id(100);
    private static readonly Guid ThinkingId = Id(101);
    private static readonly Guid AgentA = Id(20);
    private static readonly Guid AgentB = Id(21);
    private static readonly Guid Human = Id(22);

    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static GcRecord Working(string typeKey = GcTypeKeys.General, bool archived = false) =>
        new(WorkingId, null, typeKey, archived ? DateTimeOffset.UnixEpoch : null);

    private static GcMemberRecord Row(Guid gcId, Guid principal, PrincipalKind kind, GcMemberRole role, long joined, long? left = null) =>
        new(gcId, principal, kind, role, joined, left);

    private static IReadOnlyCollection<GcMemberRecord> WorkingMembers() =>
    [
        Row(WorkingId, AgentA, PrincipalKind.Agent, GcMemberRole.Member, 1),
        Row(WorkingId, AgentB, PrincipalKind.Agent, GcMemberRole.Observer, 1),
        Row(WorkingId, Human, PrincipalKind.Human, GcMemberRole.Member, 1),
    ];

    private static GcRecord Thinking() => new(ThinkingId, WorkingId, GcTypeKeys.Thinking, null);

    private static IReadOnlyCollection<GcMemberRecord> ThinkingMembers(Guid owner) =>
    [
        Row(ThinkingId, owner, PrincipalKind.Agent, GcMemberRole.Member, 0),
    ];

    [Fact]
    public void Create_nests_the_thinking_GC_under_the_working_GC()
    {
        var result = ThinkingGcRules.Create(Working(), WorkingMembers(), AgentA, ThinkingId, agentHasThinkingGc: false);

        Assert.True(result.Succeeded);
        Assert.Equal(new GcRecord(ThinkingId, WorkingId, GcTypeKeys.Thinking, null), result.Gc);
        Assert.Equal(WorkingId, result.Gc!.ParentGcId);
        Assert.Equal(new GcMemberRecord(ThinkingId, AgentA, PrincipalKind.Agent, GcMemberRole.Member, 0, null), result.Member);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Second_thinking_GC_for_the_same_agent_and_working_GC_is_refused()
    {
        var result = ThinkingGcRules.Create(Working(), WorkingMembers(), AgentA, ThinkingId, agentHasThinkingGc: true);

        Assert.Equal(GcPlanRefusal.AgentAlreadyHasThinkingGc, result.Refusal);
    }

    [Fact]
    public void Agent_that_is_not_a_writing_member_of_the_working_GC_is_refused()
    {
        var observer = ThinkingGcRules.Create(Working(), WorkingMembers(), AgentB, ThinkingId, agentHasThinkingGc: false);
        var stranger = ThinkingGcRules.Create(Working(), WorkingMembers(), Id(99), ThinkingId, agentHasThinkingGc: false);

        Assert.Equal(GcPlanRefusal.AgentNotWorkingMember, observer.Refusal);
        Assert.Equal(GcPlanRefusal.AgentNotWorkingMember, stranger.Refusal);
    }

    [Fact]
    public void Thinking_GC_cannot_be_nested_under_a_thinking_GC()
    {
        var result = ThinkingGcRules.Create(Working(GcTypeKeys.Thinking), WorkingMembers(), AgentA, ThinkingId, agentHasThinkingGc: false);

        Assert.Equal(GcPlanRefusal.WorkingGcIsThinking, result.Refusal);
    }

    [Fact]
    public void Thinking_GC_is_not_created_under_an_archived_working_GC()
    {
        var result = ThinkingGcRules.Create(Working(archived: true), WorkingMembers(), AgentA, ThinkingId, agentHasThinkingGc: false);

        Assert.Equal(GcPlanRefusal.Archived, result.Refusal);
    }

    [Fact]
    public void Owner_is_the_agent_row_with_the_lowest_JoinedSeq()
    {
        var gc = Thinking();
        var members = new List<GcMemberRecord>
        {
            Row(ThinkingId, AgentB, PrincipalKind.Agent, GcMemberRole.Member, 5),
            Row(ThinkingId, AgentA, PrincipalKind.Agent, GcMemberRole.Member, 0),
        };

        Assert.Equal(AgentA, ThinkingGcRules.OwningAgentId(gc, members));
    }

    [Fact]
    public void Thinking_GC_is_excluded_from_another_agents_context()
    {
        var gc = Thinking();

        Assert.True(ThinkingGcRules.IsExcludedFromContext(gc, ThinkingMembers(AgentA), AgentB));
    }

    [Fact]
    public void Thinking_GC_is_not_excluded_from_its_own_agent()
    {
        var gc = Thinking();

        Assert.False(ThinkingGcRules.IsExcludedFromContext(gc, ThinkingMembers(AgentA), AgentA));
    }

    [Fact]
    public void Thinking_GC_with_no_agent_row_is_excluded_for_every_agent()
    {
        var gc = Thinking();

        Assert.True(ThinkingGcRules.IsExcludedFromContext(gc, [], AgentA));
    }

    [Fact]
    public void Non_thinking_GC_is_never_excluded_by_this_rule()
    {
        var gc = Working();

        Assert.False(ThinkingGcRules.IsExcludedFromContext(gc, WorkingMembers(), AgentB));
    }

    [Fact]
    public void Thinking_GC_is_never_a_wake_target()
    {
        Assert.False(ThinkingGcRules.IsWakeTarget(Thinking()));
    }

    [Fact]
    public void Live_general_GC_is_a_wake_target_and_an_archived_one_is_not()
    {
        Assert.True(ThinkingGcRules.IsWakeTarget(Working()));
        Assert.False(ThinkingGcRules.IsWakeTarget(Working(archived: true)));
    }

    [Fact]
    public void Created_thinking_GC_is_readable_by_its_agent_only_until_approval()
    {
        var result = ThinkingGcRules.Create(Working(), WorkingMembers(), AgentA, ThinkingId, agentHasThinkingGc: false);
        var members = new List<GcMemberRecord> { result.Member! };
        var noApprovals = new HashSet<Guid>();

        Assert.Equal(GcAccessPolicy.AllEvents, GcAccessPolicy.ReadableUpToSeq(result.Gc!, members, new GcViewer(AgentA, PrincipalKind.Agent), noApprovals));
        Assert.Null(GcAccessPolicy.ReadableUpToSeq(result.Gc!, members, new GcViewer(AgentB, PrincipalKind.Agent), noApprovals));
        Assert.Equal(GcAccessPolicy.AllEvents, GcAccessPolicy.ReadableUpToSeq(result.Gc!, members, new GcViewer(AgentB, PrincipalKind.Agent), new HashSet<Guid> { AgentB }));
    }
}
