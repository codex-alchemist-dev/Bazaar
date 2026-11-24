using Bazaar.Server.Memory.Access;

namespace Bazaar.Server.CompanyKnowledge.Tests;

public sealed class CompanyAccessTests
{
    private sealed class Proposers(string? who) : ICompanyCandidateProposers
    {
        public string? ProposerOf(VaultPath candidatePath) => who;
    }

    private sealed class Quarantined(params string[] ids) : ICompanyAgentQuarantine
    {
        public bool IsQuarantined(string agentId) => ids.Contains(agentId);
    }

    private static VaultPathAccess Access(ICompanyCandidateProposers? p = null, ICompanyAgentQuarantine? q = null) =>
        new(new StaticProjectMembership(new Dictionary<string, ProjectMembers>()), null, p, q);

    private static readonly VaultPath _candidate = VaultPath.Parse("company/candidates/01J0000000000000000000AAAA.md");
    private static readonly VaultPath _admitted = VaultPath.Parse("company/memory/principle/x.md");

    [Fact]
    public void Candidates_are_readable_by_owner_maintenance_and_the_proposer_only()
    {
        var access = Access(new Proposers("charles"));
        Assert.True(access.CanRead(VaultPrincipal.Owner(), _candidate));
        Assert.True(access.CanRead(VaultPrincipal.Maintenance(), _candidate));
        Assert.True(access.CanRead(VaultPrincipal.Agent("charles"), _candidate));
        Assert.False(access.CanRead(VaultPrincipal.Agent("dana"), _candidate));
        Assert.False(access.CanRead(VaultPrincipal.Cover("cover", "charles"), _candidate));
        Assert.False(access.CanRead(VaultPrincipal.Cover("charles", "x"), _candidate));
        Assert.False(access.CanRead(VaultPrincipal.MemberHuman("m1"), _candidate));
    }

    [Fact]
    public void Candidates_fail_closed_without_a_proposer_source_or_for_an_unknown_candidate()
    {
        Assert.False(Access().CanRead(VaultPrincipal.Agent("charles"), _candidate));
        Assert.False(Access(new Proposers(null)).CanRead(VaultPrincipal.Agent("charles"), _candidate));
        Assert.True(Access().CanRead(VaultPrincipal.Owner(), _candidate));
    }

    [Fact]
    public void A_quarantined_proposer_loses_its_candidates()
    {
        Assert.False(Access(new Proposers("charles"), new Quarantined("charles")).CanRead(VaultPrincipal.Agent("charles"), _candidate));
    }

    [Fact]
    public void Candidate_paths_with_other_shapes_are_denied_to_agents()
    {
        var access = Access(new Proposers("charles"));
        Assert.False(access.CanRead(VaultPrincipal.Agent("charles"), VaultPath.Parse("company/candidates/sub/x.md")));
        Assert.False(access.CanRead(VaultPrincipal.Agent("charles"), VaultPath.Parse("company/other/x.md")));
    }

    [Fact]
    public void Admitted_company_notes_are_readable_by_agents_unless_quarantined()
    {
        Assert.True(Access().CanRead(VaultPrincipal.Agent("dana"), _admitted));
        Assert.True(Access().CanRead(VaultPrincipal.Cover("c", "dana"), _admitted));
        var access = Access(q: new Quarantined("dana"));
        Assert.False(access.CanRead(VaultPrincipal.Agent("dana"), _admitted));
        Assert.False(access.CanRead(VaultPrincipal.Cover("c", "dana"), _admitted));
        Assert.True(access.CanRead(VaultPrincipal.Agent("erin"), _admitted));
        Assert.True(access.CanRead(VaultPrincipal.Owner(), _admitted));
        Assert.True(access.CanRead(VaultPrincipal.Maintenance(), _admitted));
        Assert.False(access.CanRead(VaultPrincipal.MemberHuman("m1"), _admitted));
    }
}
