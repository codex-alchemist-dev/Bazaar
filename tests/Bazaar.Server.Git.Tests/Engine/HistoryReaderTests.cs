using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Git.Tests.Engine;

public class HistoryReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Commit(EngineFixture fx, string file, string message, DateTimeOffset when)
    {
        using var git = new Repository(fx.CloneRoot);
        File.WriteAllText(Path.Combine(fx.CloneRoot, file), message);
        Commands.Stage(git, file);
        var sig = new Signature("Dev", "dev@test.invalid", when);
        return git.Commit(message, sig, sig).Sha;
    }

    private static string Tip(EngineFixture fx)
    {
        using var git = new Repository(fx.CloneRoot);
        return git.Head.Tip.Sha;
    }

    [Fact]
    public async Task Linear_history_is_newest_first_with_parents_and_subject()
    {
        using var fx = new EngineFixture();
        var now = DateTimeOffset.UtcNow;
        var a = Commit(fx, "a.txt", "second\n\nbody text", now.AddMinutes(1));
        var b = Commit(fx, "b.txt", "third", now.AddMinutes(2));
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);

        var history = await reader.ReadHistoryAsync(EngineFixture.Repo, b, 100, Ct);

        Assert.Equal(3, history.Count);
        Assert.Equal(b, history[0].Sha);
        Assert.Equal([a], history[0].ParentShas);
        Assert.Equal("second", history[1].Subject);
        Assert.Equal("Dev", history[1].AuthorName);
        Assert.Equal("seed", history[2].Subject);
        Assert.Empty(history[2].ParentShas);
    }

    [Fact]
    public async Task Long_subject_is_capped()
    {
        using var fx = new EngineFixture();
        var sha = Commit(fx, "a.txt", new string('x', 500), DateTimeOffset.UtcNow.AddMinutes(1));
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);

        var history = await reader.ReadHistoryAsync(EngineFixture.Repo, sha, 1, Ct);

        Assert.Equal(200, history[0].Subject.Length);
    }

    [Fact]
    public async Task Merge_commit_has_two_parents()
    {
        using var fx = new EngineFixture();
        string main1, side1, merge;
        using (var git = new Repository(fx.CloneRoot))
        {
            var side = git.CreateBranch("side", git.Head.Tip);
            main1 = Commit(fx, "m.txt", "on main", DateTimeOffset.UtcNow.AddMinutes(1));
            Commands.Checkout(git, side);
            side1 = Commit(fx, "s.txt", "on side", DateTimeOffset.UtcNow.AddMinutes(2));
            Commands.Checkout(git, git.Branches["main"]);
            var sig = new Signature("Dev", "dev@test.invalid", DateTimeOffset.UtcNow.AddMinutes(3));
            merge = git.Merge(git.Branches["side"], sig, new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward }).Commit.Sha;
        }

        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);
        var history = await reader.ReadHistoryAsync(EngineFixture.Repo, merge, 100, Ct);

        Assert.Equal(4, history.Count);
        Assert.Equal(merge, history[0].Sha);
        Assert.Equal(2, history[0].ParentShas.Count);
        Assert.Contains(main1, history[0].ParentShas);
        Assert.Contains(side1, history[0].ParentShas);
    }

    [Fact]
    public async Task Refs_list_branches_and_peeled_tags_in_stable_order()
    {
        using var fx = new EngineFixture();
        var tip = Tip(fx);
        using (var git = new Repository(fx.CloneRoot))
        {
            git.CreateBranch("feature/x");
            git.ApplyTag("light");
            git.ApplyTag("annotated", tip, new Signature("T", "t@test.invalid", DateTimeOffset.UtcNow), "release");
        }

        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);
        var refs = await reader.ListRefsAsync(EngineFixture.Repo, Ct);

        Assert.Equal(["annotated", "feature/x", "light", "main"], refs.Select(r => r.Name));
        Assert.All(refs, r => Assert.Equal(tip, r.TipSha));
        Assert.Equal("tag", refs.Single(r => r.Name == "annotated").Kind);
        Assert.Equal("tag", refs.Single(r => r.Name == "light").Kind);
        Assert.Equal("branch", refs.Single(r => r.Name == "main").Kind);
    }

    [Fact]
    public async Task Remote_tracking_branches_are_listed_and_local_wins_on_a_clash()
    {
        using var fx = new EngineFixture();
        using (var git = new Repository(fx.CloneRoot))
        {
            git.Network.Push(git.Network.Remotes["origin"], "refs/heads/main:refs/heads/other", new PushOptions());
            Commands.Fetch(git, "origin", ["+refs/heads/*:refs/remotes/origin/*"], null, null);
        }

        var localMain = Commit(fx, "n.txt", "ahead", DateTimeOffset.UtcNow.AddMinutes(1));
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);
        var refs = await reader.ListRefsAsync(EngineFixture.Repo, Ct);

        Assert.Single(refs, r => r.Name == "main");
        Assert.Equal(localMain, refs.Single(r => r.Name == "main").TipSha);
        Assert.Contains(refs, r => r.Name == "other" && r.Kind == "branch");
        Assert.DoesNotContain(refs, r => r.Name.StartsWith("origin/", StringComparison.Ordinal) || r.Name == "HEAD");
    }

    [Fact]
    public async Task Limit_is_clamped_to_one_and_to_the_option_maximum()
    {
        using var fx = new EngineFixture();
        var now = DateTimeOffset.UtcNow;
        Commit(fx, "a.txt", "two", now.AddMinutes(1));
        var tip = Commit(fx, "b.txt", "three", now.AddMinutes(2));
        fx.Options.MaxHistoryCommits = 2;
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);

        Assert.Single(await reader.ReadHistoryAsync(EngineFixture.Repo, tip, 0, Ct));
        Assert.Single(await reader.ReadHistoryAsync(EngineFixture.Repo, tip, -5, Ct));
        Assert.Equal(2, (await reader.ReadHistoryAsync(EngineFixture.Repo, tip, 1000, Ct)).Count);

        fx.Options.MaxHistoryCommits = 0;
        Assert.Single(await reader.ReadHistoryAsync(EngineFixture.Repo, tip, 1000, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc12")]
    [InlineData("zzzzzzzz")]
    [InlineData("0123456789012345678901234567890123456789a")]
    public async Task Bad_sha_is_rejected(string sha)
    {
        using var fx = new EngineFixture();
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);

        await Assert.ThrowsAsync<ArgumentException>(() => reader.ReadHistoryAsync(EngineFixture.Repo, sha, 10, Ct));
    }

    [Fact]
    public async Task Unknown_sha_throws_engine_exception()
    {
        using var fx = new EngineFixture();
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);

        await Assert.ThrowsAsync<GitEngineException>(() =>
            reader.ReadHistoryAsync(EngineFixture.Repo, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", 10, Ct));
    }

    [Fact]
    public async Task Cancelled_token_stops_the_call()
    {
        using var fx = new EngineFixture();
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var tip = Tip(fx);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadHistoryAsync(EngineFixture.Repo, tip, 10, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ListRefsAsync(EngineFixture.Repo, cts.Token));
    }

    [Fact]
    public async Task Reads_wait_for_a_writer_holding_the_repo_lock()
    {
        using var fx = new EngineFixture(TimeSpan.FromMilliseconds(200));
        var reader = new LibGit2HistoryReader(fx.Options, fx.Locks);
        var tip = Tip(fx);

        using (await fx.Locks.AcquireAsync(RepoLocks.KeyForPath(fx.CloneRoot), Ct))
        {
            await Assert.ThrowsAsync<GitLockTimeoutException>(() => reader.ReadHistoryAsync(EngineFixture.Repo, tip, 10, Ct));
            await Assert.ThrowsAsync<GitLockTimeoutException>(() => reader.ListRefsAsync(EngineFixture.Repo, Ct));
        }

        Assert.Single(await reader.ReadHistoryAsync(EngineFixture.Repo, tip, 10, Ct));
    }

    [Fact]
    public void Registration_adds_the_reader_with_TryAdd()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitCredentialSource>(new FakeCredentialSource());
        services.AddBazaarGitEngine(o => o.RepoRoot = _ => "x");
        using var sp = services.BuildServiceProvider();

        Assert.IsType<LibGit2HistoryReader>(sp.GetRequiredService<IGitHistoryReader>());

        var custom = new ServiceCollection();
        var stub = new LibGit2HistoryReader(new GitEngineOptions(), new RepoLocks(TimeSpan.FromSeconds(1)));
        custom.AddSingleton<IGitHistoryReader>(stub);
        custom.AddSingleton<IGitCredentialSource>(new FakeCredentialSource());
        custom.AddBazaarGitEngine(o => o.RepoRoot = _ => "x");
        using var sp2 = custom.BuildServiceProvider();
        Assert.Same(stub, sp2.GetRequiredService<IGitHistoryReader>());
    }
}
