using Bazaar.Contracts.Gc;
using Bazaar.Server.Company.Org;
using Bazaar.Server.Org;

namespace Bazaar.Server.Tests.Company.Org;

/// <summary>Pure tests for <see cref="OrgRouting"/> and <see cref="LoanRules"/> (doc 24 section 2). No database.</summary>
public sealed class OrgRoutingTests
{
    private static readonly DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static OrgRelation Edge(
        OrgRelationKind kind, Guid from, Guid to, Guid? project, DateTimeOffset? endsAt = null, DateTimeOffset? startsAt = null) =>
        new(Guid.CreateVersion7(), kind, from, to, project, startsAt ?? _now.AddDays(-10), endsAt, Guid.CreateVersion7(), 1);

    private static OrgRouteRequest Request(IReadOnlyList<OrgRelation> relations, Guid agent, Guid? project, OrgSettings? settings = null) =>
        new(relations, agent, project, _now, settings ?? OrgSettings.Default);

    [Fact]
    public void Defaults_are_owner_approver_and_project_reports_to()
    {
        Assert.Equal(OrgRelationApprover.Owner, OrgSettings.Default.RelationApprover);
        Assert.Equal(OrgProjectItemRoute.ProjectReportsTo, OrgSettings.Default.ProjectItemRoute);
    }

    [Fact]
    public void A_project_item_routes_to_the_reports_to_in_that_project()
    {
        var agent = Guid.CreateVersion7();
        var lead = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var otherLead = Guid.CreateVersion7();
        var relations = new[]
        {
            Edge(OrgRelationKind.ReportsTo, agent, lead, project),
            Edge(OrgRelationKind.ReportsTo, agent, otherLead, Guid.CreateVersion7()),
            Edge(OrgRelationKind.ReportsTo, agent, otherLead, null),
        };

        var result = OrgRouting.Route(Request(relations, agent, project));

        Assert.False(result.ToOwner);
        Assert.Equal(lead, result.AgentId);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void A_company_item_routes_to_the_owner_even_with_a_company_reports_to()
    {
        var agent = Guid.CreateVersion7();
        var relations = new[] { Edge(OrgRelationKind.ReportsTo, agent, Guid.CreateVersion7(), null) };

        var result = OrgRouting.Route(Request(relations, agent, null));

        Assert.True(result.ToOwner);
        Assert.Null(result.AgentId);
    }

    [Fact]
    public void No_reports_to_in_the_project_falls_back_to_the_owner_with_a_reason()
    {
        var agent = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var relations = new[]
        {
            Edge(OrgRelationKind.LentTo, agent, Guid.CreateVersion7(), project),
            Edge(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), Guid.CreateVersion7(), project),
        };

        var result = OrgRouting.Route(Request(relations, agent, project));

        Assert.True(result.ToOwner);
        Assert.Null(result.AgentId);
        Assert.Contains("no live reports_to", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ended_or_not_yet_started_reports_to_is_ignored()
    {
        var agent = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var relations = new[]
        {
            Edge(OrgRelationKind.ReportsTo, agent, Guid.CreateVersion7(), project, endsAt: _now.AddDays(-1)),
            Edge(OrgRelationKind.ReportsTo, agent, Guid.CreateVersion7(), project, startsAt: _now.AddDays(1)),
        };

        Assert.True(OrgRouting.Route(Request(relations, agent, project)).ToOwner);
    }

    [Fact]
    public void The_owner_route_setting_sends_project_items_to_the_owner()
    {
        var agent = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var relations = new[] { Edge(OrgRelationKind.ReportsTo, agent, Guid.CreateVersion7(), project) };
        var settings = new OrgSettings(ProjectItemRoute: OrgProjectItemRoute.Owner);

        var result = OrgRouting.Route(Request(relations, agent, project, settings));

        Assert.True(result.ToOwner);
        Assert.Null(result.AgentId);
    }

    private static OrgActor Owner() => new(AuthorKind.Owner, Guid.CreateVersion7());

    private static OrgActor Agent(Guid id) => new(AuthorKind.Agent, id);

    private static OrgSettings LeadApproves => new(OrgRelationApprover.ProjectLead);

    [Fact]
    public void A_loan_to_oneself_is_refused_even_for_the_owner()
    {
        var agent = Guid.CreateVersion7();

        var result = LoanRules.MayStart(Owner(), agent, agent, Guid.CreateVersion7(), [], _now, OrgSettings.Default);

        Assert.False(result.Allowed);
        Assert.Contains("itself", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_owner_may_start_a_loan_under_either_approver_setting()
    {
        var lent = Guid.CreateVersion7();
        var lead = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();

        Assert.True(LoanRules.MayStart(Owner(), lent, lead, project, [], _now, OrgSettings.Default).Allowed);
        Assert.True(LoanRules.MayStart(Owner(), lent, lead, project, [], _now, LeadApproves).Allowed);
    }

    [Fact]
    public void An_agent_may_not_start_a_loan_when_the_owner_approves()
    {
        var lead = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var relations = new[] { Edge(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), lead, project) };

        var result = LoanRules.MayStart(Agent(lead), Guid.CreateVersion7(), lead, project, relations, _now, OrgSettings.Default);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void The_project_lead_may_start_a_loan_when_the_setting_allows_it()
    {
        var lead = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var relations = new[] { Edge(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), lead, project) };

        var result = LoanRules.MayStart(Agent(lead), Guid.CreateVersion7(), lead, project, relations, _now, LeadApproves);

        Assert.True(result.Allowed);
    }

    [Fact]
    public void A_lead_of_another_project_or_without_a_live_edge_is_refused()
    {
        var lead = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var elsewhere = new[] { Edge(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), lead, Guid.CreateVersion7()) };
        var ended = new[] { Edge(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), lead, project, endsAt: _now.AddDays(-1)) };

        Assert.False(LoanRules.MayStart(Agent(lead), Guid.CreateVersion7(), lead, project, elsewhere, _now, LeadApproves).Allowed);
        Assert.False(LoanRules.MayStart(Agent(lead), Guid.CreateVersion7(), lead, project, ended, _now, LeadApproves).Allowed);
    }

    [Fact]
    public void An_agent_who_is_not_the_lead_or_is_the_lent_agent_is_refused()
    {
        var lead = Guid.CreateVersion7();
        var lent = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var relations = new[] { Edge(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), lead, project) };

        Assert.False(LoanRules.MayStart(Agent(Guid.CreateVersion7()), lent, lead, project, relations, _now, LeadApproves).Allowed);
        Assert.False(LoanRules.MayStart(Agent(lent), lent, lead, project, relations, _now, LeadApproves).Allowed);
    }

    [Fact]
    public void A_human_member_may_not_start_a_loan()
    {
        var result = LoanRules.MayStart(
            new OrgActor(AuthorKind.Human, Guid.CreateVersion7()),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            [],
            _now,
            LeadApproves);

        Assert.False(result.Allowed);
    }

    [Fact]
    public void Ending_follows_the_same_approvers_as_starting()
    {
        var lead = Guid.CreateVersion7();
        var project = Guid.CreateVersion7();
        var loan = Edge(OrgRelationKind.LentTo, Guid.CreateVersion7(), lead, project);
        var scope = new[] { Edge(OrgRelationKind.ReportsTo, Guid.CreateVersion7(), lead, project) };

        Assert.True(LoanRules.MayEnd(Owner(), loan, scope, _now, OrgSettings.Default).Allowed);
        Assert.False(LoanRules.MayEnd(Agent(lead), loan, scope, _now, OrgSettings.Default).Allowed);
        Assert.True(LoanRules.MayEnd(Agent(lead), loan, scope, _now, LeadApproves).Allowed);
        Assert.False(LoanRules.MayEnd(Owner(), Edge(OrgRelationKind.ReportsTo, loan.FromAgentId, lead, project), scope, _now, LeadApproves).Allowed);
    }
}
