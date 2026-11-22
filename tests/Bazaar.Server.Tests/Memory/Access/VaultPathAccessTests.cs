using Bazaar.Server.Memory.Access;

namespace Bazaar.Server.Tests.Memory.Access;

public class VaultPathAccessTests
{
    private static readonly VaultPrincipal _owner = VaultPrincipal.Owner();
    private static readonly VaultPrincipal _maintenance = VaultPrincipal.Maintenance();
    private static readonly VaultPrincipal _alice = VaultPrincipal.Agent("alice");
    private static readonly VaultPrincipal _bob = VaultPrincipal.Agent("bob");
    private static readonly VaultPrincipal _dave = VaultPrincipal.Agent("dave");
    private static readonly VaultPrincipal _aliceCover = VaultPrincipal.Cover("eve", "alice");
    private static readonly VaultPrincipal _carol = VaultPrincipal.MemberHuman("carol");

    private static readonly VaultPathAccess _access = new(new StaticProjectMembership(
        new Dictionary<string, ProjectMembers>
        {
            ["alpha"] = new(new HashSet<string> { "alice", "bob" }, new HashSet<string> { "carol" }),
            ["beta"] = new(new HashSet<string> { "dave" }, new HashSet<string>()),
        }));

    private static readonly VaultPathAccess _hiveAccess = new(
        new StaticProjectMembership(
            new Dictionary<string, ProjectMembers>
            {
                ["alpha"] = new(new HashSet<string> { "alice", "bob" }, new HashSet<string> { "carol" }),
                ["beta"] = new(new HashSet<string> { "dave" }, new HashSet<string>()),
            }),
        new FixedHiveTargets(new Dictionary<string, string[]>
        {
            ["hive/procedure/retry.md"] = ["alpha"],
            ["hive/failure/two-targets.md"] = ["alpha", "beta"],
            ["hive/procedure/unknown-target.md"] = ["gamma"],
        }));

    private static VaultPath P(string path) => VaultPath.Parse(path);

    private sealed class FixedHiveTargets(IReadOnlyDictionary<string, string[]> targets) : IHiveCopyTargets
    {
        public IReadOnlyCollection<string> TargetProjectsOf(VaultPath copyPath) =>
            targets.TryGetValue(copyPath.Value, out var projects) ? projects : [];
    }

    [Theory]
    [InlineData("agents/bob/memory/lesson.md")]
    [InlineData("agents/bob/memory/deep/nested.md")]
    [InlineData("agents/bob/profile.md")]
    public void Agent_cannot_read_another_agents_notes(string path)
    {
        Assert.False(_access.CanRead(_alice, P(path)));
        Assert.False(_access.Explain(_alice, P(path)).Allowed);
    }

    [Fact]
    public void Agent_reads_its_own_private_notes()
    {
        Assert.True(_access.CanRead(_alice, P("agents/alice/memory/lesson.md")));
    }

    [Fact]
    public void Agent_listing_does_not_show_another_agents_private_notes()
    {
        var visible = _access.FilterVisible(_alice, new[]
        {
            P("agents/bob/memory/secret.md"),
            P("agents/alice/memory/mine.md"),
            P("agents/bob/memory/other.md"),
        });

        Assert.Equal(["agents/alice/memory/mine.md"], visible.Select(p => p.Value));
    }

    [Fact]
    public void Owner_reads_private_notes_and_the_read_is_logged()
    {
        var decision = _access.Explain(_owner, P("agents/bob/memory/lesson.md"));

        Assert.True(decision.Allowed);
        Assert.True(decision.Logged);
    }

    [Fact]
    public void Owner_read_of_a_shared_note_is_not_logged()
    {
        var decision = _access.Explain(_owner, P("projects/alpha/Index.md"));

        Assert.True(decision.Allowed);
        Assert.False(decision.Logged);
    }

    [Fact]
    public void Owner_reads_another_agents_profile_without_logging()
    {
        var decision = _access.Explain(_owner, P("agents/bob/profile.md"));

        Assert.True(decision.Allowed);
        Assert.False(decision.Logged);
    }

    [Fact]
    public void Denied_decisions_are_never_logged()
    {
        var decision = _access.Explain(_alice, P("agents/bob/memory/lesson.md"));

        Assert.False(decision.Allowed);
        Assert.False(decision.Logged);
    }

    [Fact]
    public void Agent_profile_is_readable_by_owner_and_that_agent_only()
    {
        Assert.True(_access.CanRead(_alice, P("agents/alice/profile.md")));
        Assert.False(_access.CanRead(_bob, P("agents/alice/profile.md")));
        Assert.False(_access.CanRead(_maintenance, P("agents/alice/profile.md")));
        Assert.False(_access.CanRead(_aliceCover, P("agents/alice/profile.md")));
    }

    [Fact]
    public void Cover_cannot_read_the_primarys_private_memory()
    {
        Assert.False(_access.CanRead(_aliceCover, P("agents/alice/memory/lesson.md")));
        Assert.False(_access.Explain(_aliceCover, P("agents/alice/memory/lesson.md")).Allowed);
    }

    [Fact]
    public void Cover_reads_its_own_private_memory_and_shared_project_notes_of_its_primary()
    {
        Assert.True(_access.CanRead(_aliceCover, P("agents/eve/memory/lesson.md")));
        Assert.True(_access.CanRead(_aliceCover, P("projects/alpha/Index.md")));
    }

    [Fact]
    public void Maintenance_reads_private_areas()
    {
        var decision = _access.Explain(_maintenance, P("agents/bob/memory/lesson.md"));

        Assert.True(decision.Allowed);
        Assert.False(decision.Logged);
    }

    [Fact]
    public void Peers_never_see_private_paths_in_filtered_results()
    {
        var hits = new[]
        {
            P("projects/alpha/Index.md"),
            P("agents/bob/memory/lesson.md"),
            P("agents/alice/memory/lesson.md"),
            P("company/memory/policy.md"),
        };

        foreach (var peer in new[] { _dave, _aliceCover, _carol })
        {
            var visible = _access.FilterVisible(peer, hits);
            Assert.DoesNotContain(visible, p => p.Value.StartsWith("agents/", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Link_to_a_private_target_is_refused_for_other_agents()
    {
        var from = P("projects/alpha/Index.md");
        var target = P("agents/bob/memory/lesson.md");

        Assert.False(_access.CanResolveLink(_alice, from, target));
        Assert.True(_access.CanResolveLink(_bob, from, target));
        Assert.True(_access.CanResolveLink(_owner, from, target));
    }

    [Fact]
    public void Private_note_may_link_a_shared_note()
    {
        Assert.True(_access.CanResolveLink(_bob, P("agents/bob/memory/lesson.md"), P("projects/alpha/Index.md")));
    }

    [Theory]
    [InlineData("misc/notes.md")]
    [InlineData("Agents/bob/memory/lesson.md")]
    [InlineData("company/Constitution.md")]
    [InlineData("projects/alpha")]
    [InlineData("agents/bob")]
    [InlineData("hive")]
    [InlineData("twin/alpha")]
    [InlineData("decisions/0001-x.md")]
    [InlineData("Index.md")]
    public void Unknown_paths_are_denied_by_default(string path)
    {
        Assert.False(_access.CanRead(_owner, P(path)));
        Assert.False(_access.Explain(_owner, P(path)).Allowed);
    }

    [Fact]
    public void Project_areas_are_readable_by_members_owner_and_maintenance()
    {
        Assert.True(_access.CanRead(_owner, P("projects/alpha/memory/note.md")));
        Assert.True(_access.CanRead(_maintenance, P("projects/alpha/memory/note.md")));
        Assert.True(_access.CanRead(_alice, P("projects/alpha/memory/note.md")));
        Assert.True(_access.CanRead(_carol, P("projects/alpha/deep-dives/x.md")));
    }

    [Fact]
    public void Project_areas_are_denied_to_non_members_and_unknown_projects()
    {
        Assert.False(_access.CanRead(_dave, P("projects/alpha/Index.md")));
        Assert.False(_access.CanRead(_carol, P("projects/beta/Index.md")));
        Assert.False(_access.CanRead(_alice, P("projects/gamma/Index.md")));
        Assert.True(_access.CanRead(_dave, P("projects/beta/Index.md")));
    }

    // Twin pages (twin/<project>/<page>.md, doc 25) are a project area, routed through VaultAccess.ProjectAreaOf like search.
    [Fact]
    public void Twin_pages_follow_project_membership_like_project_areas()
    {
        Assert.True(_access.CanRead(_owner, P("twin/alpha/overview.md")));
        Assert.True(_access.CanRead(_maintenance, P("twin/alpha/overview.md")));
        Assert.True(_access.CanRead(_alice, P("twin/alpha/overview.md")));
        Assert.True(_access.CanRead(_aliceCover, P("twin/alpha/overview.md")));
        Assert.True(_access.CanRead(_carol, P("twin/alpha/overview.md")));
        Assert.False(_access.CanRead(_dave, P("twin/alpha/overview.md")));
        Assert.False(_access.CanRead(_alice, P("twin/beta/overview.md")));
        Assert.False(_access.CanResolveLink(_alice, P("projects/alpha/Index.md"), P("twin/beta/overview.md")));
    }

    [Fact]
    public void Static_membership_lists_projects_by_the_same_rules_as_is_member()
    {
        var membership = new StaticProjectMembership(new Dictionary<string, ProjectMembers>
        {
            ["alpha"] = new(new HashSet<string> { "alice", "bob" }, new HashSet<string> { "carol" }),
            ["beta"] = new(new HashSet<string> { "dave", "alice" }, new HashSet<string>()),
        });

        Assert.Equal(["alpha", "beta"], membership.ProjectsOf(_alice).Order(StringComparer.Ordinal));
        Assert.Equal(["alpha", "beta"], membership.ProjectsOf(_aliceCover).Order(StringComparer.Ordinal));
        Assert.Equal(["alpha"], membership.ProjectsOf(_carol));
        Assert.Equal(["beta"], membership.ProjectsOf(_dave));
        Assert.Empty(membership.ProjectsOf(VaultPrincipal.Agent("zed")));
        Assert.Empty(membership.ProjectsOf(_owner));
        Assert.Empty(membership.ProjectsOf(_maintenance));
    }

    [Fact]
    public void Company_memory_is_readable_by_owner_maintenance_and_all_agents()
    {
        foreach (var who in new[] { _owner, _maintenance, _alice, _dave, _aliceCover })
        {
            Assert.True(_access.CanRead(who, P("company/memory/policy.md")));
        }

        Assert.False(_access.CanRead(_carol, P("company/memory/policy.md")));
    }

    // Regression: hive/** used to be readable by every agent. A copy is readable only in its grant target projects.
    [Fact]
    public void Hive_is_denied_to_every_agent_when_no_grant_data_is_available()
    {
        foreach (var who in new[] { _alice, _bob, _dave, _aliceCover, _carol })
        {
            Assert.False(_access.CanRead(who, P("hive/procedure/retry.md")));
            Assert.False(_access.Explain(who, P("hive/procedure/retry.md")).Logged);
        }
    }

    [Fact]
    public void Hive_stays_readable_by_the_owner_and_maintenance()
    {
        foreach (var access in new[] { _access, _hiveAccess })
        {
            Assert.True(access.CanRead(_owner, P("hive/procedure/retry.md")));
            Assert.True(access.CanRead(_maintenance, P("hive/procedure/retry.md")));
            Assert.True(access.CanRead(_owner, P("hive/failure/unknown.md")));
        }
    }

    [Fact]
    public void Hive_copy_is_readable_by_agents_in_a_grant_target_project()
    {
        Assert.True(_hiveAccess.CanRead(_alice, P("hive/procedure/retry.md")));
        Assert.True(_hiveAccess.CanRead(_bob, P("hive/procedure/retry.md")));
        Assert.True(_hiveAccess.CanRead(_dave, P("hive/failure/two-targets.md")));
        Assert.True(_hiveAccess.CanRead(_alice, P("hive/failure/two-targets.md")));
    }

    [Fact]
    public void Hive_copy_is_denied_to_agents_outside_its_target_projects()
    {
        Assert.False(_hiveAccess.CanRead(_dave, P("hive/procedure/retry.md")));
        Assert.False(_hiveAccess.Explain(_dave, P("hive/procedure/retry.md")).Allowed);
        Assert.False(_hiveAccess.CanRead(_alice, P("hive/procedure/unknown.md")));
        Assert.False(_hiveAccess.CanRead(_alice, P("hive/procedure/unknown-target.md")));
    }

    [Fact]
    public void Hive_copy_follows_the_cover_primarys_membership_and_never_member_humans()
    {
        Assert.True(_hiveAccess.CanRead(_aliceCover, P("hive/procedure/retry.md")));
        Assert.False(_hiveAccess.CanRead(VaultPrincipal.Cover("eve", "dave"), P("hive/procedure/retry.md")));
        Assert.False(_hiveAccess.CanRead(_carol, P("hive/procedure/retry.md")));
    }

    [Fact]
    public void Hive_link_resolution_and_filtering_follow_the_grant()
    {
        var from = P("projects/beta/Index.md");
        Assert.False(_hiveAccess.CanResolveLink(_dave, from, P("hive/procedure/retry.md")));
        Assert.True(_hiveAccess.CanResolveLink(_alice, P("projects/alpha/Index.md"), P("hive/procedure/retry.md")));

        var visible = _hiveAccess.FilterVisible(_dave, [P("hive/procedure/retry.md"), P("hive/failure/two-targets.md")]);
        Assert.Equal(["hive/failure/two-targets.md"], visible.Select(p => p.Value));
    }

    [Fact]
    public void FilterVisible_keeps_the_original_order()
    {
        var input = new[]
        {
            P("projects/alpha/z.md"),
            P("agents/bob/memory/hidden.md"),
            P("hive/b.md"),
            P("company/memory/a.md"),
            P("projects/alpha/c.md"),
            P("unknown/x.md"),
        };

        var visible = _access.FilterVisible(_alice, input);

        Assert.Equal(
            ["projects/alpha/z.md", "company/memory/a.md", "projects/alpha/c.md"],
            visible.Select(p => p.Value));
    }

    [Fact]
    public void Principal_factories_reject_identifiers_that_are_not_single_segments()
    {
        Assert.Throws<ArgumentException>(() => VaultPrincipal.Agent("../bob"));
        Assert.Throws<ArgumentException>(() => VaultPrincipal.Agent("a/b"));
        Assert.Throws<ArgumentException>(() => VaultPrincipal.Agent(""));
        Assert.Throws<ArgumentException>(() => VaultPrincipal.Cover("eve", "a/b"));
        Assert.Throws<ArgumentException>(() => VaultPrincipal.MemberHuman(".."));
    }

    [Fact]
    public void Principal_sets_only_the_identity_fields_for_its_kind()
    {
        Assert.Null(_owner.AgentId);
        Assert.Null(_maintenance.AgentId);
        Assert.Equal("alice", _alice.AgentId);
        Assert.Null(_alice.PrimaryAgentId);
        Assert.Equal("eve", _aliceCover.AgentId);
        Assert.Equal("alice", _aliceCover.PrimaryAgentId);
        Assert.Equal(VaultPrincipalKind.Cover, _aliceCover.Kind);
    }
}
