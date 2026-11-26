using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingWorkspace.Fakes;

namespace Bazaar.Server.CodingWorkspace.Tests;

public sealed class WorkShareTests
{
    [Fact]
    public void Unset_root_gives_no_share()
    {
        var source = new ConfiguredAgentWorkShareSource(new CodingWorkspaceOptions());
        Assert.Null(source.Find("charles"));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("  ")]
    public void Relative_or_blank_root_is_refused_at_construction(string root)
    {
        Assert.Throws<InvalidOperationException>(() => new ConfiguredAgentWorkShareSource(new CodingWorkspaceOptions { Root = root }));
    }

    [Fact]
    public void File_system_root_is_refused()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Throws<InvalidOperationException>(() => new ConfiguredAgentWorkShareSource(new CodingWorkspaceOptions { Root = root }));
    }

    [Fact]
    public void Work_and_passport_are_separate_siblings_under_the_root()
    {
        using var fx = new Fixture();
        var share = fx.Source.Find("charles");

        Assert.NotNull(share);
        Assert.True(WorkSharePaths.IsEqualOrUnder(share.WorktreeRoot, WorkSharePaths.Canonicalize(fx.Root)));
        Assert.True(WorkSharePaths.IsEqualOrUnder(share.PassportFolder, WorkSharePaths.Canonicalize(fx.Root)));
        Assert.False(WorkSharePaths.IsEqualOrUnder(share.WorktreeRoot, share.PassportFolder));
        Assert.False(WorkSharePaths.IsEqualOrUnder(share.PassportFolder, share.WorktreeRoot));
        Assert.EndsWith("work", share.WorktreeRoot);
        Assert.EndsWith("passport", share.PassportFolder);
    }

    [Fact]
    public void Two_agents_get_distinct_shares()
    {
        using var fx = new Fixture();
        var a = fx.Source.Find("charles")!;
        var b = fx.Source.Find("ivy")!;
        Assert.NotEqual(a.WorktreeRoot, b.WorktreeRoot);
        Assert.False(WorkSharePaths.IsEqualOrUnder(a.WorktreeRoot, b.WorktreeRoot));
        Assert.False(WorkSharePaths.IsEqualOrUnder(b.WorktreeRoot, a.WorktreeRoot));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1")]
    [InlineData("-lead")]
    [InlineData("nul\0")]
    public void Unsafe_agent_ids_get_no_share(string agentId)
    {
        using var fx = new Fixture();
        Assert.Null(fx.Source.Find(agentId));
        Assert.Throws<CodingGitException>(() => fx.Layout.GetWorktreeRoot(agentId));
    }

    [Fact]
    public void A_link_that_leaves_the_root_gives_no_share()
    {
        using var fx = new Fixture();
        var outside = Path.Combine(fx.Base, "outside");
        Directory.CreateDirectory(outside);
        var agentDir = Path.Combine(fx.Root, "agents", "evil");
        Directory.CreateDirectory(agentDir);
        if (!TryLink(Path.Combine(agentDir, "work"), outside))
        {
            return; // The host cannot create links; the escape cannot be built here.
        }

        Assert.Null(fx.Source.Find("evil"));
    }

    [Fact]
    public void A_link_from_work_onto_passport_gives_no_share()
    {
        using var fx = new Fixture();
        var agentDir = Path.Combine(fx.Root, "agents", "sneaky");
        var passport = Path.Combine(agentDir, "passport");
        Directory.CreateDirectory(passport);
        if (!TryLink(Path.Combine(agentDir, "work"), passport))
        {
            return;
        }

        Assert.Null(fx.Source.Find("sneaky"));
    }

    [Fact]
    public void Layout_refuses_an_agent_the_source_does_not_know()
    {
        var layout = new WorkShareLayout(new InMemoryAgentWorkShareSource());
        var ex = Assert.Throws<CodingGitException>(() => layout.GetWorktreeRoot("ghost"));
        Assert.Equal(CodingGitError.WorktreeOutsideWorkShare, ex.Error);
    }

    [Fact]
    public void Layout_refuses_relative_paths()
    {
        var source = new InMemoryAgentWorkShareSource();
        source.Set("a", new AgentWorkShare("work", "passport"));
        var ex = Assert.Throws<CodingGitException>(() => new WorkShareLayout(source).GetWorktreeRoot("a"));
        Assert.Equal(CodingGitError.WorktreeOutsideWorkShare, ex.Error);
    }

    [Fact]
    public void Layout_refuses_a_work_share_that_holds_or_sits_in_the_passport_folder()
    {
        using var fx = new Fixture();
        var source = new InMemoryAgentWorkShareSource();
        source.Set("inside", new AgentWorkShare(Path.Combine(fx.Base, "p", "w"), Path.Combine(fx.Base, "p")));
        source.Set("around", new AgentWorkShare(Path.Combine(fx.Base, "w"), Path.Combine(fx.Base, "w", "p")));
        source.Set("same", new AgentWorkShare(Path.Combine(fx.Base, "s"), Path.Combine(fx.Base, "s")));
        var layout = new WorkShareLayout(source);

        foreach (var agent in new[] { "inside", "around", "same" })
        {
            var ex = Assert.Throws<CodingGitException>(() => layout.GetPassportFolder(agent));
            Assert.Equal(CodingGitError.WorktreeOnPassportFolder, ex.Error);
        }
    }

    [Fact]
    public void Layout_matches_the_service_placement_rule()
    {
        using var fx = new Fixture();
        Assert.Equal(fx.Source.Find("charles")!.WorktreeRoot, fx.Layout.GetWorktreeRoot("charles"));
        Assert.Equal(fx.Source.Find("charles")!.PassportFolder, fx.Layout.GetPassportFolder("charles"));
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
