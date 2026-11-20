using System.Text;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.SealedGit;
using Bazaar.Server.SealedStorage;
using Bazaar.Server.Vault.Git;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.SealedVault.Tests;

/// <summary>The sealed Vault over the real LibGit2 provider (doc 61 "Vault adoption").</summary>
public sealed class SealedGitProviderTests : IDisposable
{
    private const string _canary = "CANARY-sealed-vault-note-7731";
    private static readonly GitIdentity _writer = new("Writer", "writer@example.invalid");
    private static readonly GitIdentity _agent = new("agent-a", "agent-a@example.invalid");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-sealedvault-test-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _audit = new();
    private readonly InMemorySealAnchor _anchor = new();

    public SealedGitProviderTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(TempRoot);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private string MountRoot => Path.Combine(_dir, "mnt");

    private string RepoPath => Path.Combine(MountRoot, "vault");

    private string WorktreeRoot => Path.Combine(MountRoot, "vault.memory-worktrees");

    private string SealedRoot => Path.Combine(_dir, "sealed");

    private string TempRoot => Path.Combine(_dir, "tmp");

    private SealedStore NewStore() => new(new SealedStoreOptions { RootPath = SealedRoot }, FileKekProvider.Open(Path.Combine(_dir, "kek.key")), _anchor, _audit);

    private ISealedGitRepo NewRepo() => new SealedGitRepoFactory(NewStore(), _audit, new SealedGitOptions { TempRoot = TempRoot }).Open("acme");

    private SealedGitProvider NewProvider(string? importFrom = null) =>
        new(new LibGit2GitProvider(), NewRepo(), new SealedVaultOptions("acme", MountRoot, RepoPath, WorktreeRoot, importFrom), _audit);

    /// <summary>Writes a file into the main checkout and commits it, as the Vault writer does.</summary>
    private async Task<string> CommitMainAsync(IGitProvider git, string relative, string text)
    {
        // The Vault writer reads HEAD before it writes; on a fresh, unborn repo the tests mount explicitly instead.
        if (git is ISealedVault vault)
        {
            await vault.EnsureMountedAsync();
        }

        var full = Path.Combine(RepoPath, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, text);
        return await git.CommitAsync(RepoPath, "Write " + relative, _writer, CancellationToken.None);
    }

    /// <summary>The memory batch flow of GitMemoryVault: branch + worktree, write, commit, merge with trailers, remove the worktree, prune.</summary>
    private async Task<MergeResult> MemoryBatchAsync(SealedGitProvider git, string id, string text)
    {
        var branch = $"agent/memory-agent-a-{id}";
        var worktree = Path.Combine(WorktreeRoot, branch.Replace('/', '-'));
        Directory.CreateDirectory(WorktreeRoot);
        await git.AddWorktreeAsync(RepoPath, branch, worktree, CancellationToken.None);
        var note = Path.Combine(worktree, "agents", "agent-a", "memory", id + ".md");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        await File.WriteAllTextAsync(note, text);
        await git.CommitAsync(worktree, "memory: " + id + "\n\nBazaar-Agent: agent-a\n", _agent, CancellationToken.None);
        var merge = await git.MergeAsync(RepoPath, branch, _writer, CancellationToken.None, "Merge memory " + id + "\n\nBazaar-Agent: agent-a\n");
        Directory.Delete(worktree, recursive: true);
        await git.PruneWorktreesAsync(RepoPath, CancellationToken.None);
        return merge;
    }

    private IEnumerable<string> FilesOutside(string? excluded) =>
        Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)
            .Where(f => excluded is null || !f.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => !f.EndsWith("kek.key", StringComparison.Ordinal))
            // The held scope lease is an empty, exclusively locked file.
            .Where(f => !f.EndsWith("scope.lease", StringComparison.Ordinal));

    private void AssertNoCanaryOnDisk(string? excluded)
    {
        foreach (var file in FilesOutside(excluded))
        {
            Assert.DoesNotContain(_canary, Encoding.Latin1.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
            Assert.DoesNotContain("agent-a", Path.GetFileName(file), StringComparison.Ordinal);
        }

        // Git objects are compressed, so the canary alone cannot prove the git dir stayed inside; no git dir layout may exist outside either.
        var gitDirs = Directory.EnumerateDirectories(_dir, "*", SearchOption.AllDirectories)
            .Where(d => excluded is null || !d.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(d => Path.GetFileName(d) is ".git" or "objects" or "refs");
        Assert.Empty(gitDirs);
    }

    [Fact]
    public async Task Commit_merge_and_worktree_flow_round_trips_and_leaves_no_plaintext_outside_the_mount()
    {
        await using (var git = NewProvider())
        {
            await CommitMainAsync(git, "README.md", "vault\n");
            var merge = await MemoryBatchAsync(git, "n1", _canary);
            Assert.Equal(GitMergeOutcome.Merged, merge.Outcome);

            var head = await git.HeadAsync(RepoPath, CancellationToken.None);
            Assert.Equal(merge.CommitSha, head);
            Assert.Equal(_canary, await git.ReadTextAsync(RepoPath, head, "agents/agent-a/memory/n1.md", CancellationToken.None));
            Assert.Equal(_canary, await File.ReadAllTextAsync(Path.Combine(RepoPath, "agents", "agent-a", "memory", "n1.md")));
            Assert.Empty(await git.ListWorktreesAsync(RepoPath, CancellationToken.None));

            // While mounted, the plaintext is only under the mount root.
            AssertNoCanaryOnDisk(MountRoot);
        }

        // After dispose nothing is left anywhere: the checkout, the worktree root and their markers are gone.
        AssertNoCanaryOnDisk(null);
        Assert.False(Directory.Exists(RepoPath));
        Assert.False(Directory.Exists(WorktreeRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(MountRoot));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.mount");
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.unmount");
    }

    [Fact]
    public async Task A_reseal_after_merge_survives_a_restart()
    {
        string head;
        await using (var first = NewProvider())
        {
            await CommitMainAsync(first, "README.md", "vault\n");
            await MemoryBatchAsync(first, "n1", _canary);
            head = await first.HeadAsync(RepoPath, CancellationToken.None);
        }

        await using var second = NewProvider();
        Assert.Equal(head, await second.HeadAsync(RepoPath, CancellationToken.None));
        Assert.Equal(_canary, await second.ReadTextAsync(RepoPath, head, "agents/agent-a/memory/n1.md", CancellationToken.None));
        Assert.Equal(_canary, await File.ReadAllTextAsync(Path.Combine(RepoPath, "agents", "agent-a", "memory", "n1.md")));
        var branchTip = await second.ResolveCommitAsync(RepoPath, "agent/memory-agent-a-n1", CancellationToken.None);
        Assert.Contains(branchTip, (await second.ListCommitsAsync(RepoPath, null, head, CancellationToken.None)).Select(c => c.Sha));

        // The rebuilt checkout and index accept new work on top.
        var next = await MemoryBatchAsync(second, "n2", "second note");
        Assert.Equal(GitMergeOutcome.Merged, next.Outcome);
        Assert.Contains(head, (await second.ListCommitsAsync(RepoPath, null, next.CommitSha!, CancellationToken.None)).Select(c => c.Sha));
    }

    [Fact]
    public async Task A_tampered_sealed_file_fails_closed_on_mount_and_leaves_nothing_on_disk()
    {
        await using (var git = NewProvider())
        {
            await CommitMainAsync(git, "notes/a.md", _canary);
        }

        foreach (var file in Directory.GetFiles(SealedRoot, "*.bzs", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            bytes[^20] ^= 0x01;
            File.WriteAllBytes(file, bytes);
        }

        await using var tampered = NewProvider();
        await Assert.ThrowsAsync<SealTamperedException>(() => tampered.HeadAsync(RepoPath, CancellationToken.None));
        Assert.False(tampered.IsMounted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(MountRoot));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.mount.failed");

        // The lease was released: a second try fails on the tamper again, not on a lease timeout.
        await Assert.ThrowsAsync<SealTamperedException>(() => tampered.HeadAsync(RepoPath, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_reseal_faults_the_vault_and_every_later_call_fails_closed()
    {
        await using var git = NewProvider();
        await CommitMainAsync(git, "README.md", "vault\n");

        // A writer that ignores the lease changes the sealed ref; the next reseal's generation check catches it.
        using (var other = await NewStore().OpenScopeAsync("vault:acme"))
        {
            await other.WriteAsync("refs/heads/main", Encoding.ASCII.GetBytes(new string('a', 40) + "\n"));
        }

        await Assert.ThrowsAsync<SealGenerationConflictException>(() => CommitMainAsync(git, "b.md", "two"));
        Assert.True(git.IsFaulted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.HeadAsync(RepoPath, CancellationToken.None));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.mount.reseal_failed");
    }

    [Fact]
    public async Task A_stale_mount_is_wiped_before_mounting()
    {
        await using (var git = NewProvider())
        {
            await CommitMainAsync(git, "README.md", "vault\n");
        }

        // A crash leaves the checkout, a worktree and their markers behind.
        Directory.CreateDirectory(Path.Combine(RepoPath, "stale"));
        await File.WriteAllTextAsync(Path.Combine(RepoPath, "stale", "left.md"), _canary);
        await File.WriteAllBytesAsync(RepoPath + ".bazaar-sealed-mount", []);
        Directory.CreateDirectory(Path.Combine(WorktreeRoot, "agent-memory-x"));
        await File.WriteAllTextAsync(Path.Combine(WorktreeRoot, "agent-memory-x", "left.md"), _canary);
        await File.WriteAllBytesAsync(WorktreeRoot + ".bazaar-sealed-mount", []);

        await using var git2 = NewProvider();
        await git2.EnsureMountedAsync();
        Assert.False(Directory.Exists(Path.Combine(RepoPath, "stale")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(WorktreeRoot));
        Assert.True(File.Exists(Path.Combine(RepoPath, "README.md")));
        Assert.Equal(2, _audit.Events.Count(e => e.Kind == "sealgit.mount.stale_wiped"));
        AssertNoCanaryOnDisk(MountRoot);
        Assert.DoesNotContain(Directory.EnumerateFiles(MountRoot, "*", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains(_canary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plaintext_the_mount_did_not_make_is_refused_and_left_alone()
    {
        Directory.CreateDirectory(RepoPath);
        var foreign = Path.Combine(RepoPath, "mine.md");
        await File.WriteAllTextAsync(foreign, "keep me");

        await using var git = NewProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.EnsureMountedAsync());
        Assert.Equal("keep me", await File.ReadAllTextAsync(foreign));
        Assert.False(File.Exists(RepoPath + ".bazaar-sealed-mount"));
    }

    [Fact]
    public async Task A_vault_worktree_outside_the_worktree_root_is_refused()
    {
        await using var git = NewProvider();
        await CommitMainAsync(git, "README.md", "vault\n");
        var outside = Path.Combine(_dir, "elsewhere", "wt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.AddWorktreeAsync(RepoPath, "agent/x", outside, CancellationToken.None));
        Assert.False(Directory.Exists(outside));
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.HeadAsync(Path.Combine(MountRoot, "other"), CancellationToken.None));
        Assert.Contains(_audit.Events, e => e.Kind == "sealvault.refused");
    }

    [Fact]
    public async Task Other_repositories_pass_through_without_a_mount()
    {
        var other = Path.Combine(_dir, "other-repo");
        LibGit2Sharp.Repository.Init(other);
        await File.WriteAllTextAsync(Path.Combine(other, "a.txt"), "x");

        await using var git = NewProvider();
        var sha = await git.CommitAsync(other, "plain", _writer, CancellationToken.None);
        Assert.Equal(sha, await git.HeadAsync(other, CancellationToken.None));
        Assert.False(git.IsMounted);
        Assert.False(Directory.Exists(SealedRoot) && Directory.EnumerateFiles(SealedRoot, "*.bzs", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task An_existing_plaintext_repo_is_imported_verified_and_wiped()
    {
        var source = Path.Combine(_dir, "plain-vault");
        LibGit2Sharp.Repository.Init(source);
        var engine = new LibGit2GitProvider();
        await File.WriteAllTextAsync(Path.Combine(source, "note.md"), _canary);
        var head = await engine.CommitAsync(source, "first", _writer, CancellationToken.None);

        await using (var git = NewProvider(importFrom: source))
        {
            Assert.Equal(head, await git.HeadAsync(RepoPath, CancellationToken.None));
            Assert.Equal(_canary, await git.ReadTextAsync(RepoPath, head, "note.md", CancellationToken.None));
            Assert.False(Directory.Exists(source));
            Assert.Contains(_audit.Events, e => e.Kind == "sealgit.import");
        }

        AssertNoCanaryOnDisk(null);

        // The next start finds no source and mounts the sealed repo.
        await using var again = NewProvider(importFrom: source);
        Assert.Equal(head, await again.HeadAsync(RepoPath, CancellationToken.None));
    }

    [Fact]
    public async Task A_dirty_plaintext_repo_is_not_imported_and_nothing_is_lost()
    {
        var source = Path.Combine(_dir, "plain-vault");
        LibGit2Sharp.Repository.Init(source);
        await File.WriteAllTextAsync(Path.Combine(source, "note.md"), "committed");
        await new LibGit2GitProvider().CommitAsync(source, "first", _writer, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(source, "draft.md"), "not committed");

        await using var git = NewProvider(importFrom: source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => git.EnsureMountedAsync());
        Assert.Equal("not committed", await File.ReadAllTextAsync(Path.Combine(source, "draft.md")));
        Assert.True(Directory.Exists(Path.Combine(source, ".git")));
        using var scope = await NewStore().OpenScopeAsync("vault:acme");
        Assert.Empty(await scope.ListAsync());
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.import.failed");
    }

    [Fact]
    public void Disabled_by_default_leaves_the_registered_provider_untouched()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitProvider, LibGit2GitProvider>();
        var before = services.Single(d => d.ServiceType == typeof(IGitProvider));

        new SealedVaultComponent().AddServices(services, new ConfigurationBuilder().Build());

        Assert.Same(before, services.Single(d => d.ServiceType == typeof(IGitProvider)));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISealedVault));
    }

    [Fact]
    public async Task Enabled_decorates_the_registered_provider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitProvider, LibGit2GitProvider>();
        services.AddSingleton<ISealAuditSink>(_audit);
        services.AddSingleton<ISealedGitRepoFactory>(new SealedGitRepoFactory(NewStore(), _audit, new SealedGitOptions { TempRoot = TempRoot }));
        new SealedVaultComponent().AddServices(services, Config(new()
        {
            ["Bazaar:SealedVault:Enabled"] = "true",
            ["Bazaar:SealedVault:Name"] = "acme",
            ["Bazaar:SealedVault:MountRoot"] = MountRoot,
            ["Memory:Vault:RepoPath"] = RepoPath,
        }));

        await using var provider = services.BuildServiceProvider();
        var git = provider.GetRequiredService<IGitProvider>();
        Assert.IsType<SealedGitProvider>(git);
        Assert.Single(provider.GetServices<IGitProvider>());
        Assert.Equal("vault:acme", provider.GetRequiredService<ISealedVault>().Scope);
        await CommitMainAsync(git, "README.md", "vault\n");
    }

    [Fact]
    public void Enabled_with_the_repo_outside_the_mount_root_fails_the_start()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitProvider, LibGit2GitProvider>();
        Assert.Throws<InvalidOperationException>(() => new SealedVaultComponent().AddServices(services, Config(new()
        {
            ["Bazaar:SealedVault:Enabled"] = "true",
            ["Bazaar:SealedVault:Name"] = "acme",
            ["Bazaar:SealedVault:MountRoot"] = MountRoot,
            ["Memory:Vault:RepoPath"] = Path.Combine(_dir, "plain"),
        })));
        Assert.Throws<InvalidOperationException>(() => new SealedVaultComponent().AddServices(new ServiceCollection(), Config(new()
        {
            ["Bazaar:SealedVault:Enabled"] = "true",
            ["Bazaar:SealedVault:Name"] = "acme",
            ["Memory:Vault:RepoPath"] = RepoPath,
        })));
    }

    [Fact]
    public void The_module_does_not_reference_the_server_assembly()
    {
        var names = typeof(SealedGitProvider).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
        Assert.DoesNotContain("Bazaar.Server", names);
        Assert.DoesNotContain("Bazaar.Server.SealedGit", names);
        Assert.DoesNotContain("LibGit2Sharp", names);
    }

    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class RecordingSink : ISealAuditSink
    {
        private readonly List<SealAuditEvent> _events = [];

        public IReadOnlyList<SealAuditEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}
