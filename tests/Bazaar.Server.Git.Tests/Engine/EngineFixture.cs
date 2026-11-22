using LibGit2Sharp;

namespace Bazaar.Server.Git.Tests.Engine;

/// <summary>A credential source that records every lease and hands out a known secret.</summary>
internal sealed class FakeCredentialSource : IGitCredentialSource
{
    public const string Secret = "s3cr3t-token-value-xyz";

    public List<(RepoRef Repo, GitOperation Operation, GitCredential Credential)> Leases { get; } = [];

    public Task<GitCredential> LeaseAsync(RepoRef repo, GitOperation operation, CancellationToken cancellationToken)
    {
        var credential = new GitCredential("x-access-token", Secret, DateTimeOffset.UtcNow.AddMinutes(5));
        Leases.Add((repo, operation, credential));
        return Task.FromResult(credential);
    }
}

/// <summary>
/// Temp directories with a main clone (<c>main</c> branch, one commit), a local bare remote it pushes to, and a worktree root.
/// Everything is removed on dispose.
/// </summary>
internal sealed class EngineFixture : IDisposable
{
    public static readonly RepoRef Repo = new("github", "acme/widgets");

    public EngineFixture(TimeSpan? lockTimeout = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "bazaar-git-engine-" + Guid.NewGuid().ToString("N"));
        BareRoot = Path.Combine(Root, "remote.git");
        CloneRoot = Path.Combine(Root, "clone");
        WorktreeRoot = Path.Combine(Root, "worktrees");
        Directory.CreateDirectory(Root);

        Repository.Init(BareRoot, isBare: true);
        File.WriteAllText(Path.Combine(BareRoot, "HEAD"), "ref: refs/heads/main\n");
        Repository.Init(CloneRoot);
        File.WriteAllText(Path.Combine(CloneRoot, ".git", "HEAD"), "ref: refs/heads/main\n");
        using (var clone = new Repository(CloneRoot))
        {
            File.WriteAllText(Path.Combine(CloneRoot, "README.md"), "hello\n");
            Commands.Stage(clone, "README.md");
            var sig = new Signature("Seed", "seed@test.invalid", DateTimeOffset.UtcNow);
            clone.Commit("seed", sig, sig);
            clone.Network.Remotes.Add("origin", BareRoot);
            clone.Network.Push(clone.Network.Remotes["origin"], "refs/heads/main:refs/heads/main", new PushOptions());
        }

        Options = new GitEngineOptions
        {
            RepoLockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30),
            WorktreeRoot = WorktreeRoot,
            RepoRoot = _ => CloneRoot,
            KnownRepos = () => [Repo],
            BotAuthor = new GitSignatureIdentity("Agent Alice", "alice@agents.invalid"),
            HostCommitter = new GitSignatureIdentity("Bazaar Host", "host@bazaar.invalid"),
        };
        Credentials = new FakeCredentialSource();
        Locks = new RepoLocks(Options.RepoLockTimeout);
        Provider = new LibGit2GitProvider(Options, Credentials, Locks);
    }

    public string Root { get; }

    public string BareRoot { get; }

    public string CloneRoot { get; }

    public string WorktreeRoot { get; }

    public GitEngineOptions Options { get; }

    public FakeCredentialSource Credentials { get; }

    public RepoLocks Locks { get; }

    public LibGit2GitProvider Provider { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        // Git object files are read-only on some platforms.
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}
