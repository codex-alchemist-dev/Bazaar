using LibGit2Sharp;

namespace Bazaar.Server.Git.Tests.Engine;

public class PushFetchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WorktreeHandle> WorktreeWithCommit(EngineFixture fixture, string branch)
    {
        var handle = await fixture.Provider.AddWorktreeAsync(EngineFixture.Repo, new BranchName(branch), Ct);
        await File.WriteAllTextAsync(Path.Combine(handle.Path, "work.txt"), "work", Ct);
        await fixture.Provider.CommitAsync(handle, new CommitRequest("work", ["work.txt"]), Ct);
        return handle;
    }

    [Fact]
    public async Task Push_sends_the_branch_to_the_bare_remote_and_disposes_the_credential()
    {
        using var fixture = new EngineFixture();
        var handle = await WorktreeWithCommit(fixture, "agent/push");

        await fixture.Provider.PushAsync(new WorkspaceRef(handle.Path), handle.Branch, Ct);

        using var bare = new Repository(fixture.BareRoot);
        Assert.NotNull(bare.Branches["agent/push"]);
        var lease = Assert.Single(fixture.Credentials.Leases);
        Assert.Equal(GitOperation.Push, lease.Operation);
        Assert.Equal(EngineFixture.Repo, lease.Repo);
        Assert.Throws<ObjectDisposedException>(() => lease.Credential.Reveal());
    }

    [Fact]
    public async Task Failed_push_never_leaks_the_secret_and_still_disposes_the_credential()
    {
        using var fixture = new EngineFixture();
        var handle = await WorktreeWithCommit(fixture, "agent/broken");
        using (var clone = new Repository(fixture.CloneRoot))
        {
            clone.Network.Remotes.Remove("origin");
            clone.Network.Remotes.Add("origin", Path.Combine(fixture.Root, "does-not-exist.git"));
        }

        var ex = await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.PushAsync(new WorkspaceRef(handle.Path), handle.Branch, Ct));

        Assert.DoesNotContain(FakeCredentialSource.Secret, ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
        var lease = Assert.Single(fixture.Credentials.Leases);
        Assert.Throws<ObjectDisposedException>(() => lease.Credential.Reveal());
    }

    [Fact]
    public async Task Rejected_push_reports_without_the_secret()
    {
        using var fixture = new EngineFixture();
        var handle = await WorktreeWithCommit(fixture, "agent/ff");
        await fixture.Provider.PushAsync(new WorkspaceRef(handle.Path), handle.Branch, Ct);

        // Make the remote branch diverge, then push the worktree again: not a fast-forward.
        using (var bare = new Repository(fixture.BareRoot))
        {
            var sig = new Signature("Other", "other@test.invalid", DateTimeOffset.UtcNow);
            var tree = bare.Lookup<Commit>(bare.Branches["main"].Tip.Sha)!.Tree;
            var diverged = bare.ObjectDatabase.CreateCommit(sig, sig, "diverged", tree, [], false);
            bare.Refs.UpdateTarget(bare.Refs["refs/heads/agent/ff"], diverged.Id);
        }

        await File.WriteAllTextAsync(Path.Combine(handle.Path, "more.txt"), "more", Ct);
        await fixture.Provider.CommitAsync(handle, new CommitRequest("more", ["more.txt"]), Ct);

        var ex = await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.PushAsync(new WorkspaceRef(handle.Path), handle.Branch, Ct));
        Assert.DoesNotContain(FakeCredentialSource.Secret, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("MAIN")]
    public async Task Push_to_main_or_master_is_refused_before_any_credential_is_leased(string target)
    {
        using var fixture = new EngineFixture();
        var handle = await WorktreeWithCommit(fixture, "agent/work");

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.PushAsync(new WorkspaceRef(handle.Path), new BranchName(target), Ct));
        Assert.Empty(fixture.Credentials.Leases);
    }

    [Fact]
    public async Task Push_to_the_repos_default_branch_is_refused()
    {
        using var fixture = new EngineFixture();
        fixture.Options.DefaultBranch = _ => "develop";
        var handle = await WorktreeWithCommit(fixture, "develop");

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.PushAsync(new WorkspaceRef(handle.Path), handle.Branch, Ct));
        Assert.Empty(fixture.Credentials.Leases);
        using var bare = new Repository(fixture.BareRoot);
        Assert.Null(bare.Branches["develop"]);
    }

    [Fact]
    public async Task Push_refuses_a_branch_that_is_not_checked_out_in_the_workspace()
    {
        using var fixture = new EngineFixture();
        var handle = await WorktreeWithCommit(fixture, "agent/one");

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.PushAsync(new WorkspaceRef(handle.Path), new BranchName("agent/two"), Ct));
        Assert.Empty(fixture.Credentials.Leases);
    }

    [Fact]
    public async Task Push_refuses_a_workspace_the_engine_did_not_create()
    {
        using var fixture = new EngineFixture();

        await Assert.ThrowsAsync<GitEngineException>(
            () => fixture.Provider.PushAsync(new WorkspaceRef(Path.Combine(fixture.Root, "nowhere")), new BranchName("agent/x"), Ct));
    }

    [Fact]
    public async Task Fetch_brings_in_new_remote_commits_with_a_read_lease()
    {
        using var fixture = new EngineFixture();
        using (var other = new Repository(Repository.Clone(fixture.BareRoot, Path.Combine(fixture.Root, "other"))))
        {
            await File.WriteAllTextAsync(Path.Combine(other.Info.WorkingDirectory, "new.txt"), "n", Ct);
            Commands.Stage(other, "new.txt");
            var sig = new Signature("Other", "other@test.invalid", DateTimeOffset.UtcNow);
            other.Commit("remote change", sig, sig);
            other.Network.Push(other.Network.Remotes["origin"], "refs/heads/main:refs/heads/main", new PushOptions());
        }

        await fixture.Provider.FetchAsync(EngineFixture.Repo, Ct);

        using var clone = new Repository(fixture.CloneRoot);
        Assert.Equal("remote change", clone.Branches["origin/main"].Tip.MessageShort);
        var lease = Assert.Single(fixture.Credentials.Leases);
        Assert.Equal(GitOperation.Read, lease.Operation);
        Assert.Throws<ObjectDisposedException>(() => lease.Credential.Reveal());
    }
}
