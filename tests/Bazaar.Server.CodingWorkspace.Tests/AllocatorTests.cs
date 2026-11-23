using Bazaar.Server.CodingGit;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.CodingWorkspace.Tests;

public sealed class AllocatorTests
{
    private static WorktreeAllocation Writer(string sub, string parent = "p1") => new(sub, parent, false, []);

    private static WorktreeAllocation Reader(string sub, string parent = "p1") => new(sub, parent, true, []);

    [Fact]
    public async Task Writer_gets_its_own_worktree_on_a_child_branch_under_the_parents_share()
    {
        using var fx = new Fixture();
        fx.AddParent();

        var lease = await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None);

        Assert.False(lease.ReadOnly);
        var root = fx.Source.Find("charles")!.WorktreeRoot;
        Assert.True(WorkSharePaths.IsEqualOrUnder(lease.Path, root));
        Assert.NotEqual(fx.ParentPath(), lease.Path);
        var branch = fx.Engine.BranchAt(lease.Path);
        Assert.NotNull(branch);
        Assert.StartsWith("agent/fix-login-sub-1-", branch.Value.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_writers_get_distinct_paths_and_branches()
    {
        using var fx = new Fixture();
        fx.AddParent();

        var a = await fx.Allocator.AllocateAsync(Writer("sub-a"), CancellationToken.None);
        var b = await fx.Allocator.AllocateAsync(Writer("sub-b"), CancellationToken.None);

        Assert.NotEqual(a.Path, b.Path);
        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(fx.Engine.BranchAt(a.Path), fx.Engine.BranchAt(b.Path));
    }

    [Fact]
    public async Task Ids_that_sanitise_alike_still_get_different_branches()
    {
        using var fx = new Fixture();
        fx.AddParent();

        var a = await fx.Allocator.AllocateAsync(Writer("Sub_1"), CancellationToken.None);
        var b = await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None);

        Assert.NotEqual(a.Path, b.Path);
    }

    [Fact]
    public async Task Concurrent_allocation_of_many_subagents_gives_distinct_paths()
    {
        using var fx = new Fixture();
        fx.AddParent();

        var leases = await Task.WhenAll(Enumerable.Range(0, 24)
            .Select(i => Task.Run(() => fx.Allocator.AllocateAsync(Writer($"sub-{i}"), CancellationToken.None))));

        Assert.Equal(24, leases.Select(l => l.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(24, leases.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Concurrent_allocation_for_one_subagent_succeeds_exactly_once()
    {
        using var fx = new Fixture();
        fx.AddParent();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            try
            {
                return await fx.Allocator.AllocateAsync(Writer("same"), CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (CodingGitException)
            {
                return null;
            }
        }));

        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public async Task Release_frees_the_worktree_and_a_second_release_is_a_no_op()
    {
        using var fx = new Fixture();
        fx.AddParent();
        var first = await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None);

        await fx.Allocator.ReleaseAsync(first, CancellationToken.None);
        await fx.Allocator.ReleaseAsync(first, CancellationToken.None);

        var again = await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None);
        Assert.Equal(first.Path, again.Path);
        Assert.NotEqual(first.Id, again.Id);
    }

    [Fact]
    public async Task A_held_writer_blocks_a_second_allocation_for_the_same_subagent()
    {
        using var fx = new Fixture();
        fx.AddParent();
        await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None));
    }

    [Fact]
    public async Task Engine_failure_leaves_nothing_held_and_the_subagent_can_retry()
    {
        using var fx = new Fixture();
        fx.AddParent();
        fx.Engine.FailNextWith = new IOException("disk");

        await Assert.ThrowsAsync<IOException>(() => fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None));

        var lease = await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task Cancellation_before_start_throws_and_holds_nothing()
    {
        using var fx = new Fixture();
        fx.AddParent();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => fx.Allocator.AllocateAsync(Writer("sub-1"), cts.Token));

        Assert.NotNull(await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_parent_fails_closed()
    {
        using var fx = new Fixture();

        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Allocator.AllocateAsync(Writer("sub-1", "nobody"), CancellationToken.None));
    }

    [Fact]
    public async Task Blank_ids_are_refused()
    {
        using var fx = new Fixture();
        fx.AddParent();

        await Assert.ThrowsAsync<CodingGitException>(() => fx.Allocator.AllocateAsync(Writer(" "), CancellationToken.None));
        await Assert.ThrowsAsync<CodingGitException>(() => fx.Allocator.AllocateAsync(Writer("sub-1", ""), CancellationToken.None));
    }

    [Fact]
    public async Task A_parent_with_no_share_fails_closed()
    {
        using var fx = new Fixture();
        fx.Parents.Set("p1", new SubagentParentWork("../evil", Fixture.Repo, "gc-1", "task-7", "fix-login", "/x"));

        await Assert.ThrowsAsync<CodingGitException>(() => fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None));
    }

    [Fact]
    public async Task Long_parent_slug_and_odd_subagent_id_still_give_a_valid_branch()
    {
        using var fx = new Fixture();
        var slug = new string('a', 100);
        fx.Parents.Set("p1", new SubagentParentWork("charles", Fixture.Repo, "gc-1", "task-7", slug, fx.ParentPath(slug: slug)));

        var lease = await fx.Allocator.AllocateAsync(Writer("Sub Agent/../ÄÖ" + new string('x', 200)), CancellationToken.None);

        var branch = fx.Engine.BranchAt(lease.Path);
        Assert.NotNull(branch);
        Assert.True(branch.Value.TaskSlug.Length <= BranchName.MaxTaskLength);
    }

    [Fact]
    public async Task Read_only_lease_is_the_parents_path_and_adds_no_git_state()
    {
        using var fx = new Fixture();
        fx.AddParent();

        var lease = await fx.Allocator.AllocateAsync(Reader("sub-1"), CancellationToken.None);

        Assert.True(lease.ReadOnly);
        Assert.Equal(WorkSharePaths.Canonicalize(fx.ParentPath()), lease.Path);
        Assert.Empty(fx.Engine.WorktreePaths);
    }

    [Fact]
    public async Task Read_only_lease_for_a_path_outside_the_share_is_refused()
    {
        using var fx = new Fixture();
        fx.Parents.Set("p1", new SubagentParentWork("charles", Fixture.Repo, "gc-1", "task-7", "fix-login", Path.Combine(fx.Base, "elsewhere")));

        var ex = await Assert.ThrowsAsync<CodingGitException>(() => fx.Allocator.AllocateAsync(Reader("sub-1"), CancellationToken.None));
        Assert.Equal(CodingGitError.WorktreeOutsideWorkShare, ex.Error);
    }

    [Fact]
    public async Task Read_only_lease_on_the_passport_folder_is_refused()
    {
        using var fx = new Fixture();
        var passport = fx.Source.Find("charles")!.PassportFolder;
        fx.Parents.Set("p1", new SubagentParentWork("charles", Fixture.Repo, "gc-1", "task-7", "fix-login", passport));

        await Assert.ThrowsAsync<CodingGitException>(() => fx.Allocator.AllocateAsync(Reader("sub-1"), CancellationToken.None));
    }

    [Fact]
    public async Task Read_only_lease_for_a_dotdot_path_is_refused()
    {
        using var fx = new Fixture();
        var share = fx.Source.Find("charles")!.WorktreeRoot;
        fx.Parents.Set("p1", new SubagentParentWork("charles", Fixture.Repo, "gc-1", "task-7", "fix-login", share + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "passport"));

        await Assert.ThrowsAsync<CodingGitException>(() => fx.Allocator.AllocateAsync(Reader("sub-1"), CancellationToken.None));
    }

    [Fact]
    public async Task Forged_or_unknown_lease_release_does_nothing()
    {
        using var fx = new Fixture();
        fx.AddParent();
        var real = await fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None);

        await fx.Allocator.ReleaseAsync(new WorktreeLease("nope", real.Path, false), CancellationToken.None);
        await fx.Allocator.ReleaseAsync(new WorktreeLease(real.Id, real.Path + "x", false), CancellationToken.None);

        // Still held: the same subagent cannot allocate again.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fx.Allocator.AllocateAsync(Writer("sub-1"), CancellationToken.None));
    }
}
