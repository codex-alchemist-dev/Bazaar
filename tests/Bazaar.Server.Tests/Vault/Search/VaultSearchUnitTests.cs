using Bazaar.Server.Memory.Access;
using Bazaar.Server.Vault.Search;

namespace Bazaar.Server.Tests.Vault.Search;

/// <summary>Pure search rules: RRF with fixed ranks, the access rule, and card text. No database.</summary>
public sealed class VaultSearchUnitTests
{
    [Fact]
    public void Rrf_scores_each_list_by_rank_and_sums_keys_that_appear_in_both()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([["a", "b", "c"], ["b", "d"]], k: 60);

        // b is rank 2 in the first list and rank 1 in the second; a is rank 1 in one list only.
        Assert.Equal(["b", "a", "d", "c"], fused.Select(entry => entry.Key));
        Assert.Equal(1.0 / 62 + 1.0 / 61, fused[0].Score, precision: 12);
        Assert.Equal(1.0 / 61, fused[1].Score, precision: 12);
        Assert.Equal(1.0 / 63, fused[3].Score, precision: 12);
    }

    [Fact]
    public void Rrf_breaks_ties_by_first_seen_order()
    {
        var fused = ReciprocalRankFusion.Fuse<string>([["x"], ["y"]], k: 10);

        Assert.Equal(["x", "y"], fused.Select(entry => entry.Key));
        Assert.Equal(fused[0].Score, fused[1].Score);
    }

    [Fact]
    public void Rrf_rejects_a_non_positive_constant()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReciprocalRankFusion.Fuse<string>([["a"]], k: 0));
    }

    private static readonly VaultCaller _alpha = new(IsOwner: false, AgentOwner: "agent:alpha");
    private static readonly VaultCaller _beta = new(IsOwner: false, AgentOwner: "agent:beta");
    private static readonly VaultCaller _anonymous = new(IsOwner: false, AgentOwner: null);

    [Fact]
    public void Private_documents_are_readable_only_by_their_agent_or_the_owner()
    {
        const string path = "agents/alpha/memory/plan.md";
        Assert.True(VaultAccess.CanRead(path, "agent:alpha", _alpha));
        Assert.False(VaultAccess.CanRead(path, "agent:alpha", _beta));
        Assert.False(VaultAccess.CanRead(path, "agent:alpha", _anonymous));
        Assert.True(VaultAccess.CanRead(path, "agent:alpha", VaultCaller.Owner));
    }

    [Theory]
    [InlineData("company/Constitution.md")]
    [InlineData("company/memory/policy.md")]
    [InlineData("shared/research/auth.md")]
    public void Ownerless_documents_in_company_and_shared_areas_are_readable_by_every_agent(string path)
    {
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, _alpha));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, _beta));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, VaultCaller.Owner));
    }

    // Regression: an ownerless document used to be readable by every caller, so Vault search ignored project membership.
    [Theory]
    [InlineData("projects/x/Plan.md")]
    [InlineData("projects/x/deep-dives/auth.md")]
    [InlineData("hive/procedure/retry-backoff.md")]
    [InlineData("decisions/0001-use-postgres.md")]
    [InlineData("drafts/alpha/idea.md")]
    [InlineData("twin/x/overview.md")]
    [InlineData("Index.md")]
    [InlineData("agents/alpha/charter.md")]
    [InlineData("companyish/notes.md")]
    public void Ownerless_documents_outside_an_agent_readable_area_are_the_owners_only(string path)
    {
        Assert.False(VaultAccess.CanRead(path, documentOwner: null, _beta));
        Assert.False(VaultAccess.CanRead(path, documentOwner: null, _anonymous));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, VaultCaller.Owner));
    }

    [Fact]
    public void An_agent_reads_ownerless_files_in_its_own_area_only()
    {
        Assert.True(VaultAccess.CanRead("agents/alpha/charter.md", documentOwner: null, _alpha));
        Assert.False(VaultAccess.CanRead("agents/alpha/charter.md", documentOwner: null, _beta));
        Assert.False(VaultAccess.CanRead("agents/alphabet/charter.md", documentOwner: null, _alpha));
    }

    [Fact]
    public void A_caller_that_is_neither_owner_nor_agent_reads_nothing()
    {
        Assert.False(VaultAccess.CanRead("company/Constitution.md", documentOwner: null, _anonymous));
        Assert.False(VaultAccess.CanRead("shared/research/auth.md", documentOwner: null, _anonymous));
    }

    [Fact]
    public void An_owner_field_overrides_an_agent_readable_area()
    {
        Assert.False(VaultAccess.CanRead("shared/memory/alpha.md", "agent:alpha", _beta));
        Assert.True(VaultAccess.CanRead("shared/memory/alpha.md", "agent:alpha", _alpha));
    }

    // Project membership (follow-up to the fail-closed rule): alpha is a member of project x only.
    private static readonly VaultCaller _alphaInX = new(IsOwner: false, AgentOwner: "agent:alpha") { Projects = new HashSet<string> { "x" } };

    [Theory]
    [InlineData("projects/x/Index.md")]
    [InlineData("projects/x/Plan.md")]
    [InlineData("projects/x/deep-dives/auth.md")]
    [InlineData("projects/x/memory/lesson.md")]
    [InlineData("twin/x/overview.md")]
    public void A_member_reads_the_ownerless_documents_of_its_own_project(string path)
    {
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, _alphaInX));
    }

    [Theory]
    [InlineData("projects/y/Plan.md")]
    [InlineData("twin/y/overview.md")]
    [InlineData("projects/xy/Plan.md")]
    [InlineData("projects/x.md")]
    [InlineData("projects/x")]
    [InlineData("twin/x")]
    [InlineData("project/x/Plan.md")]
    [InlineData("other/projects/x/Plan.md")]
    public void A_member_does_not_read_another_project_or_a_path_that_only_looks_like_its_own(string path)
    {
        Assert.False(VaultAccess.CanRead(path, documentOwner: null, _alphaInX));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, VaultCaller.Owner));
    }

    // These areas are not project-scoped by path in doc 18, so membership never opens them.
    [Theory]
    [InlineData("decisions/0001-use-postgres.md")]
    [InlineData("drafts/alpha/idea.md")]
    [InlineData("archive/2026-10.md")]
    [InlineData("hive/procedure/retry-backoff.md")]
    [InlineData("Index.md")]
    public void Membership_does_not_open_areas_that_are_not_project_scoped(string path)
    {
        Assert.False(VaultAccess.CanRead(path, documentOwner: null, _alphaInX));
        Assert.True(VaultAccess.CanRead(path, documentOwner: null, VaultCaller.Owner));
    }

    [Fact]
    public void Empty_membership_denies_every_project_area()
    {
        var noProjects = new VaultCaller(IsOwner: false, AgentOwner: "agent:alpha") { Projects = new HashSet<string>() };

        Assert.Empty(_alpha.Projects);
        Assert.False(VaultAccess.CanRead("projects/x/Plan.md", documentOwner: null, noProjects));
        Assert.False(VaultAccess.CanRead("projects/x/Plan.md", documentOwner: null, _alpha));
        Assert.False(VaultAccess.CanRead("twin/x/overview.md", documentOwner: null, _alpha));
    }

    [Fact]
    public void A_caller_with_projects_but_no_agent_identity_reads_nothing()
    {
        var noIdentity = new VaultCaller(IsOwner: false, AgentOwner: null) { Projects = new HashSet<string> { "x" } };

        Assert.False(VaultAccess.CanRead("projects/x/Plan.md", documentOwner: null, noIdentity));
        Assert.False(VaultAccess.CanRead("twin/x/overview.md", documentOwner: null, noIdentity));
    }

    [Fact]
    public void Membership_does_not_open_another_agents_private_document_in_a_project_area()
    {
        Assert.False(VaultAccess.CanRead("projects/x/memory/beta.md", "agent:beta", _alphaInX));
        Assert.True(VaultAccess.CanRead("projects/x/memory/beta.md", "agent:beta", _beta));
    }

    [Theory]
    [InlineData("projects/x/Plan.md", "x")]
    [InlineData("projects/x/deep/nested.md", "x")]
    [InlineData("twin/core-api/page.md", "core-api")]
    [InlineData("projects/x", null)]
    [InlineData("projects/x/", null)]
    [InlineData("projects//Plan.md", null)]
    [InlineData("projects/x.md", null)]
    [InlineData("decisions/0001-x.md", null)]
    [InlineData("hive/x/a.md", null)]
    [InlineData("Projects/x/Plan.md", null)]
    public void Project_area_of_takes_the_second_segment_of_a_project_root_only(string path, string? expected)
    {
        Assert.Equal(expected, VaultAccess.ProjectAreaOf(path));
    }

    [Fact]
    public void Caller_projects_are_copied_so_later_changes_to_the_source_do_not_widen_access()
    {
        var source = new HashSet<string> { "x" };
        var caller = new VaultCaller(IsOwner: false, AgentOwner: "agent:alpha") { Projects = source };
        source.Add("y");

        Assert.False(VaultAccess.CanRead("projects/y/Plan.md", documentOwner: null, caller));
        Assert.Equal(["x"], caller.Projects);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x/y")]
    public void Caller_projects_reject_a_slug_that_is_not_one_path_segment(string slug)
    {
        Assert.Throws<ArgumentException>(() => new VaultCaller(IsOwner: false, AgentOwner: "agent:alpha") { Projects = new HashSet<string> { slug } });
    }

    private static readonly Guid _agentId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000a1");
    private static readonly Guid _coverId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000c3");
    private static readonly Guid _outsiderId = Guid.Parse("0196a7c4-0000-7000-8000-0000000000d4");

    private static readonly StaticProjectMembership _membership = new(new Dictionary<string, ProjectMembers>
    {
        ["x"] = new(new HashSet<string> { _agentId.ToString("D") }, new HashSet<string>()),
        ["y"] = new(new HashSet<string> { _outsiderId.ToString("D") }, new HashSet<string>()),
        ["bad/slug"] = new(new HashSet<string> { _agentId.ToString("D") }, new HashSet<string>()),
    });

    [Fact]
    public void An_agent_caller_resolves_its_projects_from_membership_at_request_time()
    {
        var caller = VaultAccess.ForAgent(_agentId, _membership);

        Assert.Equal(VaultOwner.ForAgent(_agentId), caller.AgentOwner);
        Assert.False(caller.IsOwner);
        Assert.Equal(["x"], caller.Projects);
        Assert.True(VaultAccess.CanRead("projects/x/Plan.md", documentOwner: null, caller));
        Assert.False(VaultAccess.CanRead("projects/y/Plan.md", documentOwner: null, caller));
    }

    [Fact]
    public void A_cover_caller_follows_its_primarys_projects_and_keeps_its_own_identity()
    {
        var cover = VaultAccess.ForCover(_coverId, _agentId, _membership);

        Assert.Equal(VaultOwner.ForAgent(_coverId), cover.AgentOwner);
        Assert.Equal(["x"], cover.Projects);
        Assert.True(VaultAccess.CanRead("projects/x/Plan.md", documentOwner: null, cover));
        Assert.False(VaultAccess.CanRead("projects/y/Plan.md", documentOwner: null, cover));
        Assert.False(VaultAccess.CanRead("agents/" + _agentId.ToString("D") + "/memory/a.md", VaultOwner.ForAgent(_agentId), cover));
    }

    [Fact]
    public void A_non_member_or_an_unresolved_membership_carries_no_projects()
    {
        var stranger = VaultAccess.ForAgent(Guid.Parse("0196a7c4-0000-7000-8000-0000000000e5"), _membership);
        var unresolved = VaultAccess.ForAgent(_agentId, membership: null);

        Assert.Empty(stranger.Projects);
        Assert.Empty(unresolved.Projects);
        Assert.False(VaultAccess.CanRead("projects/x/Plan.md", documentOwner: null, stranger));
        Assert.False(VaultAccess.CanRead("projects/x/Plan.md", documentOwner: null, unresolved));
    }

    [Fact]
    public void Gist_collapses_whitespace_and_truncates_with_an_ellipsis()
    {
        Assert.Equal("one two three", VaultSearchService.Gist("one\n\n  two\tthree", 200));
        Assert.Equal("one tw...", VaultSearchService.Gist("one two three four", 9));
    }

    [Fact]
    public void Approximate_tokens_round_up_at_four_characters_per_token()
    {
        Assert.Equal(0, VaultSearchService.ApproxTokens(""));
        Assert.Equal(1, VaultSearchService.ApproxTokens("abcd"));
        Assert.Equal(2, VaultSearchService.ApproxTokens("abcde"));
    }
}
