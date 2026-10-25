using System.Text.Json;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Members;

public class GcMembershipPlannerTests
{
    private static readonly Guid OwnerId = Id(1);
    private static readonly Guid MemberA = Id(2);
    private static readonly Guid Observer = Id(4);
    private static readonly Guid Former = Id(5);
    private static readonly Guid Outsider = Id(6);
    private static readonly Guid NewPrincipal = Id(9);
    private static readonly Guid GcId = Id(100);
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private const long HeadSeq = 15;
    private const long FormerLeftSeq = 10;

    private static readonly GcViewer Owner = new(OwnerId, PrincipalKind.Owner);
    private static readonly GcViewer Member = new(MemberA, PrincipalKind.Human);
    private static readonly GcViewer ObserverViewer = new(Observer, PrincipalKind.Human);
    private static readonly GcViewer FormerViewer = new(Former, PrincipalKind.Human);
    private static readonly GcViewer OutsiderViewer = new(Outsider, PrincipalKind.Human);

    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static GcRecord General(bool archived = false) =>
        new(GcId, null, GcTypeKeys.General, archived ? Now : null);

    private static GcRecord Dm() => new(GcId, null, GcTypeKeys.Dm, null);

    private static GcRecord Thinking() => new(GcId, Id(200), GcTypeKeys.Thinking, null);

    private static GcMemberRecord Row(Guid principal, PrincipalKind kind, GcMemberRole role, long joined, long? left) =>
        new(GcId, principal, kind, role, joined, left);

    private static IReadOnlyCollection<GcMemberRecord> GeneralMembers() =>
    [
        Row(MemberA, PrincipalKind.Human, GcMemberRole.Member, 1, null),
        Row(Observer, PrincipalKind.Human, GcMemberRole.Observer, 1, null),
        Row(Former, PrincipalKind.Human, GcMemberRole.Member, 1, FormerLeftSeq),
    ];

    private static IReadOnlyCollection<GcMemberRecord> DmMembers() =>
    [
        Row(MemberA, PrincipalKind.Human, GcMemberRole.Member, 1, null),
        Row(Id(3), PrincipalKind.Agent, GcMemberRole.Member, 1, null),
    ];

    private static IReadOnlySet<Guid> NoApprovals => new HashSet<Guid>();

    private static string Action(GcEventDraft? ev) =>
        JsonElement.Parse(ev!.PayloadJson!).GetProperty("action").GetString()!;

    // Add member

    [Fact]
    public void Owner_adds_a_member_with_JoinedSeq_at_head_plus_one()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), Owner, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.True(result.Succeeded);
        Assert.Equal(new GcMemberRecord(GcId, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq + 1, null), result.Member);
        Assert.Null(result.Gc);
        Assert.Equal(GcEventKinds.System, result.Event!.Kind);
        Assert.Equal("member_added", Action(result.Event));
        Assert.Equal(AuthorKind.Owner, result.Event.AuthorKind);
    }

    [Fact]
    public void Member_may_invite_under_any_member_policy()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), Member, NewPrincipal, PrincipalKind.Agent, GcMemberRole.Observer, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.True(result.Succeeded);
        Assert.Equal(GcMemberRole.Observer, result.Member!.Role);
        Assert.Equal(AuthorKind.Human, result.Event!.AuthorKind);
        Assert.Equal(SourceTrust.MemberHuman, result.Event.SourceTrust);
    }

    [Fact]
    public void Member_invite_is_refused_under_owner_only_policy()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), Member, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.OwnerOnly);

        Assert.Equal(GcPlanRefusal.OwnerOnlyInvites, result.Refusal);
        Assert.Null(result.Member);
        Assert.Null(result.Event);
    }

    [Fact]
    public void Owner_invite_is_allowed_under_owner_only_policy()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), Owner, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.OwnerOnly);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Observer_cannot_invite()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), ObserverViewer, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.NotPermitted, result.Refusal);
    }

    [Fact]
    public void Former_member_and_outsider_cannot_invite()
    {
        var former = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), FormerViewer, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);
        var outsider = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), OutsiderViewer, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.NotPermitted, former.Refusal);
        Assert.Equal(GcPlanRefusal.NotPermitted, outsider.Refusal);
    }

    [Fact]
    public void Adding_an_active_member_is_refused()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), Owner, MemberA, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.AlreadyMember, result.Refusal);
    }

    [Fact]
    public void Re_adding_a_former_member_creates_a_new_active_row()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), Owner, Former, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.True(result.Succeeded);
        Assert.Null(result.Member!.LeftSeq);
        Assert.Equal(HeadSeq + 1, result.Member.JoinedSeq);
    }

    [Fact]
    public void Owner_cannot_be_added_as_a_row()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(), GeneralMembers(), Owner, NewPrincipal, PrincipalKind.Owner, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.OwnerHasNoRow, result.Refusal);
    }

    [Fact]
    public void Adding_to_an_archived_GC_is_refused()
    {
        var result = GcMembershipPlanner.PlanAddMember(General(archived: true), GeneralMembers(), Owner, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.Archived, result.Refusal);
    }

    [Fact]
    public void A_DM_takes_no_third_member()
    {
        var result = GcMembershipPlanner.PlanAddMember(Dm(), DmMembers(), Owner, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.DmFull, result.Refusal);
    }

    [Fact]
    public void Thinking_GC_membership_is_fixed()
    {
        var result = GcMembershipPlanner.PlanAddMember(Thinking(), [Row(MemberA, PrincipalKind.Agent, GcMemberRole.Member, 0, null)], Owner, NewPrincipal, PrincipalKind.Human, GcMemberRole.Member, HeadSeq, GcInvitePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.ThinkingMembershipFixed, result.Refusal);
    }

    // Remove member

    [Fact]
    public void Member_removes_themselves_and_LeftSeq_is_head_plus_one()
    {
        var result = GcMembershipPlanner.PlanRemoveMember(General(), GeneralMembers(), Member, MemberA, HeadSeq);

        Assert.True(result.Succeeded);
        Assert.Equal(HeadSeq + 1, result.Member!.LeftSeq);
        Assert.Equal(MemberA, result.Member.PrincipalId);
        Assert.Equal(GcMemberRole.Member, result.Member.Role);
        Assert.Equal("member_removed", Action(result.Event));
    }

    [Fact]
    public void Observer_may_leave_themselves()
    {
        var result = GcMembershipPlanner.PlanRemoveMember(General(), GeneralMembers(), ObserverViewer, Observer, HeadSeq);

        Assert.True(result.Succeeded);
        Assert.Equal(GcMemberRole.Observer, result.Member!.Role);
    }

    [Fact]
    public void Member_cannot_remove_another_member()
    {
        var result = GcMembershipPlanner.PlanRemoveMember(General(), GeneralMembers(), Member, Observer, HeadSeq);

        Assert.Equal(GcPlanRefusal.NotPermitted, result.Refusal);
        Assert.Null(result.Member);
    }

    [Fact]
    public void Owner_removes_a_member()
    {
        var result = GcMembershipPlanner.PlanRemoveMember(General(), GeneralMembers(), Owner, MemberA, HeadSeq);

        Assert.True(result.Succeeded);
        Assert.Equal(HeadSeq + 1, result.Member!.LeftSeq);
    }

    [Fact]
    public void Removing_a_former_member_has_no_active_row()
    {
        var result = GcMembershipPlanner.PlanRemoveMember(General(), GeneralMembers(), Owner, Former, HeadSeq);

        Assert.Equal(GcPlanRefusal.NoActiveMembership, result.Refusal);
    }

    [Fact]
    public void Removing_from_an_archived_GC_is_refused()
    {
        var result = GcMembershipPlanner.PlanRemoveMember(General(archived: true), GeneralMembers(), Owner, MemberA, HeadSeq);

        Assert.Equal(GcPlanRefusal.Archived, result.Refusal);
    }

    // Archive and unarchive

    [Fact]
    public void Owner_archives_and_the_header_gets_ArchivedAt()
    {
        var result = GcMembershipPlanner.PlanArchive(General(), Owner, Now);

        Assert.True(result.Succeeded);
        Assert.Equal(Now, result.Gc!.ArchivedAt);
        Assert.Equal("archived", Action(result.Event));
        Assert.Null(result.Member);
    }

    [Fact]
    public void Member_cannot_archive()
    {
        var result = GcMembershipPlanner.PlanArchive(General(), Member, Now);

        Assert.Equal(GcPlanRefusal.NotPermitted, result.Refusal);
    }

    [Fact]
    public void Archiving_twice_is_refused()
    {
        var first = GcMembershipPlanner.PlanArchive(General(), Owner, Now);
        var second = GcMembershipPlanner.PlanArchive(first.Gc!, Owner, Now);

        Assert.Equal(GcPlanRefusal.AlreadyArchived, second.Refusal);
    }

    [Fact]
    public void Owner_unarchives_under_owner_policy()
    {
        var result = GcMembershipPlanner.PlanUnarchive(General(archived: true), GeneralMembers(), Owner, GcUnarchivePolicy.Owner);

        Assert.True(result.Succeeded);
        Assert.Null(result.Gc!.ArchivedAt);
        Assert.Equal("unarchived", Action(result.Event));
    }

    [Fact]
    public void Member_cannot_unarchive_under_owner_policy()
    {
        var result = GcMembershipPlanner.PlanUnarchive(General(archived: true), GeneralMembers(), Member, GcUnarchivePolicy.Owner);

        Assert.Equal(GcPlanRefusal.NotPermitted, result.Refusal);
    }

    [Fact]
    public void Member_unarchives_under_any_member_policy()
    {
        var result = GcMembershipPlanner.PlanUnarchive(General(archived: true), GeneralMembers(), Member, GcUnarchivePolicy.AnyMember);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Observer_and_former_member_cannot_unarchive_even_under_any_member_policy()
    {
        var observer = GcMembershipPlanner.PlanUnarchive(General(archived: true), GeneralMembers(), ObserverViewer, GcUnarchivePolicy.AnyMember);
        var former = GcMembershipPlanner.PlanUnarchive(General(archived: true), GeneralMembers(), FormerViewer, GcUnarchivePolicy.AnyMember);

        Assert.Equal(GcPlanRefusal.NotPermitted, observer.Refusal);
        Assert.Equal(GcPlanRefusal.NotPermitted, former.Refusal);
    }

    [Fact]
    public void Unarchiving_a_live_GC_is_refused()
    {
        var result = GcMembershipPlanner.PlanUnarchive(General(), GeneralMembers(), Owner, GcUnarchivePolicy.Owner);

        Assert.Equal(GcPlanRefusal.NotArchived, result.Refusal);
    }

    // Access requests and decisions

    [Fact]
    public void Outsider_requests_access_to_a_DM()
    {
        var result = GcMembershipPlanner.PlanAccessRequest(Dm(), DmMembers(), NoApprovals, OutsiderViewer, HeadSeq, requestPending: false);

        Assert.True(result.Succeeded);
        Assert.Equal("access_requested", Action(result.Event));
        Assert.Equal(Outsider, result.Event!.AuthorId);
    }

    [Fact]
    public void Non_DM_and_non_thinking_GC_takes_no_request()
    {
        var result = GcMembershipPlanner.PlanAccessRequest(General(), GeneralMembers(), NoApprovals, OutsiderViewer, HeadSeq, requestPending: false);

        Assert.Equal(GcPlanRefusal.NotRequestable, result.Refusal);
    }

    [Fact]
    public void DM_member_cannot_request_access_they_already_have()
    {
        var result = GcMembershipPlanner.PlanAccessRequest(Dm(), DmMembers(), NoApprovals, Member, HeadSeq, requestPending: false);

        Assert.Equal(GcPlanRefusal.AlreadyReadable, result.Refusal);
    }

    [Fact]
    public void Former_member_below_head_may_request_again()
    {
        var result = GcMembershipPlanner.PlanAccessRequest(Dm(), DmMembers(), NoApprovals, FormerViewer, HeadSeq, requestPending: false);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Duplicate_pending_request_is_refused()
    {
        var result = GcMembershipPlanner.PlanAccessRequest(Dm(), DmMembers(), NoApprovals, OutsiderViewer, HeadSeq, requestPending: true);

        Assert.Equal(GcPlanRefusal.DuplicatePendingRequest, result.Refusal);
    }

    [Fact]
    public void Owner_approves_a_DM_request()
    {
        var result = GcMembershipPlanner.PlanAccessDecision(Dm(), DmMembers(), Owner, Outsider, approve: true, GcDmAccessApprovers.Owner, requestPending: true);

        Assert.True(result.Succeeded);
        Assert.Equal("access_approved", Action(result.Event));
        Assert.Equal(Outsider.ToString("D"), JsonElement.Parse(result.Event!.PayloadJson!).GetProperty("principal").GetString());
    }

    [Fact]
    public void Owner_denies_a_request()
    {
        var result = GcMembershipPlanner.PlanAccessDecision(Dm(), DmMembers(), Owner, Outsider, approve: false, GcDmAccessApprovers.Owner, requestPending: true);

        Assert.True(result.Succeeded);
        Assert.Equal("access_denied", Action(result.Event));
    }

    [Fact]
    public void DM_member_approves_under_owner_or_dm_member_policy()
    {
        var result = GcMembershipPlanner.PlanAccessDecision(Dm(), DmMembers(), Member, Outsider, approve: true, GcDmAccessApprovers.OwnerOrDmMember, requestPending: true);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void DM_member_is_refused_under_owner_policy()
    {
        var result = GcMembershipPlanner.PlanAccessDecision(Dm(), DmMembers(), Member, Outsider, approve: true, GcDmAccessApprovers.Owner, requestPending: true);

        Assert.Equal(GcPlanRefusal.NotApprover, result.Refusal);
    }

    [Fact]
    public void Non_member_cannot_approve_even_under_owner_or_dm_member_policy()
    {
        var result = GcMembershipPlanner.PlanAccessDecision(Dm(), DmMembers(), OutsiderViewer, Id(77), approve: true, GcDmAccessApprovers.OwnerOrDmMember, requestPending: true);

        Assert.Equal(GcPlanRefusal.NotApprover, result.Refusal);
    }

    [Fact]
    public void Deciding_without_a_pending_request_is_refused()
    {
        var result = GcMembershipPlanner.PlanAccessDecision(Dm(), DmMembers(), Owner, Outsider, approve: true, GcDmAccessApprovers.Owner, requestPending: false);

        Assert.Equal(GcPlanRefusal.NoPendingRequest, result.Refusal);
    }

    [Fact]
    public void Decision_on_an_archived_GC_is_refused()
    {
        var result = GcMembershipPlanner.PlanAccessDecision(Dm() with { ArchivedAt = Now }, DmMembers(), Owner, Outsider, approve: true, GcDmAccessApprovers.Owner, requestPending: true);

        Assert.Equal(GcPlanRefusal.Archived, result.Refusal);
    }
}
