using Bazaar.Server.Vault.Git;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Git;

/// <summary>
/// A throwaway repository under the system temp path, removed on <see cref="Dispose"/>. Fixture writes go through
/// LibGit2Sharp directly, so the provider under test never builds its own inputs.
/// </summary>
internal sealed class TempGitRepository : IDisposable
{
    public static readonly GitIdentity Writer = new("Test Writer", "writer@example.invalid");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-vlt3-" + Guid.NewGuid().ToString("N"));

    public TempGitRepository(params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(Checkout);
        using (var repo = new Repository(Repository.Init(Checkout)))
        {
            repo.Refs.UpdateTarget("HEAD", "refs/heads/main");
            repo.Config.Set("user.name", Writer.Name);
            repo.Config.Set("user.email", Writer.Email);
            repo.Config.Set("core.autocrlf", "false");
        }

        foreach (var (path, content) in files)
        {
            WriteFile(Checkout, path, content);
        }

        CommitAll(Checkout, "initial");
    }

    /// <summary>The main checkout (branch main).</summary>
    public string Checkout => Path.Combine(_root, "main");

    /// <summary>A worktree directory path under the same temp root; not created.</summary>
    public string WorktreePath(string name) => Path.Combine(_root, name);

    public static string WriteFile(string workdir, string relativePath, string content)
    {
        var full = Path.Combine(workdir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public static string ReadFile(string workdir, string relativePath) =>
        File.ReadAllText(Path.Combine(workdir, relativePath));

    /// <summary>Stages every change in the working directory and commits it on the checked-out branch.</summary>
    public static string CommitAll(string workdir, string message)
    {
        using var repo = new Repository(workdir);
        Commands.Stage(repo, "*");
        var signature = new Signature(Writer.Name, Writer.Email, DateTimeOffset.UtcNow);
        return repo.Commit(message, signature, signature).Sha;
    }

    public static string TipOf(string repoPath, string branch)
    {
        using var repo = new Repository(repoPath);
        return repo.Branches[branch].Tip.Sha;
    }

    public static string HeadOf(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Head.Tip.Sha;
    }

    /// <summary>Count of working-tree or index changes (ignored files excluded). Zero means a clean checkout.</summary>
    public static int DirtyEntryCount(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true }).Count(e => e.State != FileStatus.Ignored);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        // Git object files are read-only on Windows; clear the attribute so the tree can be deleted.
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}

/// <summary>
/// Object-database check run in place of <c>git fsck --strict</c> (the git CLI is not a test dependency): every commit
/// reachable from a local branch is readable, its parents and whole tree resolve, and every blob it names exists.
/// </summary>
internal static class GitIntegrity
{
    public static void AssertReachableObjectsIntact(string repoPath)
    {
        using var repo = new Repository(repoPath);
        var filter = new CommitFilter { IncludeReachableFrom = repo.Branches.Where(b => !b.IsRemote).Select(b => b.Tip) };
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            Assert.NotNull(repo.Lookup<Commit>(commit.Sha));
            AssertTreeIntact(repo, commit.Tree);
        }
    }

    private static void AssertTreeIntact(Repository repo, Tree tree)
    {
        Assert.NotNull(repo.Lookup<Tree>(tree.Id));
        foreach (var entry in tree)
        {
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                AssertTreeIntact(repo, (Tree)entry.Target);
            }
            else
            {
                Assert.NotNull(repo.Lookup<Blob>(entry.Target.Id));
            }
        }
    }
}
