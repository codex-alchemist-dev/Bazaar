using System.Text;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedGit.Tests;

/// <summary>Mount, stale-mount and import rules of sealed git (doc 61 "Vault adoption").</summary>
public sealed class SealedGitMountTests : IDisposable
{
    private const string _canary = "CANARY-sealed-mount-5521";
    private static readonly IMountRootVerifier _protected = new FixedVerifier(true);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-sealedmount-test-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _audit = new();
    private readonly InMemorySealAnchor _anchor = new();

    public SealedGitMountTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "tmp"));
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

    private SealedStore NewStore() => new(new SealedStoreOptions { RootPath = Path.Combine(_dir, "sealed") }, FileKekProvider.Open(Path.Combine(_dir, "kek.key")), _anchor, _audit);

    private SealedGitRepo Repo() => (SealedGitRepo)new SealedGitRepoFactory(NewStore(), _audit, new SealedGitOptions { TempRoot = Path.Combine(_dir, "tmp") }).Open("acme");

    private async Task<string[]> SealedPaths()
    {
        using var scope = await NewStore().OpenScopeAsync("vault:acme");
        return (await scope.ListAsync()).Select(e => e.Path).ToArray();
    }

    private static async Task Git(string gitDir, string? workTree, params string[] args)
    {
        var r = await GitCommand.RunAsync(gitDir, args, workTree);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)} failed: {r.StandardError}");
    }

    private static Task CommitAll(string gitDir, string workTree, string message) =>
        Git(gitDir, workTree, "-c", "user.name=T", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", message);

    /// <summary>A plaintext checkout with one commit of <paramref name="text"/>.</summary>
    private async Task<string> PlainRepoAsync(string name, string text)
    {
        var source = Path.Combine(_dir, name);
        Directory.CreateDirectory(source);
        var gitDir = Path.Combine(source, ".git");
        await Git(gitDir, source, "init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(source, "note.md"), text);
        await Git(gitDir, source, "add", "note.md");
        await CommitAll(gitDir, source, "one");

        // The runner makes an empty hooks folder next to the git dir; it is not part of the checkout.
        Directory.Delete(Path.Combine(source, ".nohooks"));
        return source;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        }

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
        }
    }

    [Fact]
    public async Task A_mount_on_an_unconfirmed_volume_is_refused_and_audited()
    {
        var repo = Repo();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.MountAsync(new SealedGitMountRequest(MountRoot, Path.Combine(MountRoot, "vault"), new FixedVerifier(false))));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.mount_unverified");
        Assert.False(Directory.Exists(Path.Combine(MountRoot, "vault")));
    }

    [Fact]
    public async Task Link_parents_link_escapes_and_prefix_siblings_are_refused()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var real = Path.Combine(_dir, "real");
        Directory.CreateDirectory(real);
        var alias = Path.Combine(_dir, "alias");
        Directory.CreateSymbolicLink(alias, real);
        var repo = Repo();

        // A mount root below a link.
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.MountAsync(new SealedGitMountRequest(Path.Combine(alias, "mnt"), Path.Combine(alias, "mnt", "vault"), _protected)));

        // A prefix sibling of the root ("mnt-x" next to "mnt").
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.MountAsync(new SealedGitMountRequest(MountRoot, Path.Combine(MountRoot + "-x", "vault"), _protected)));

        // A checkout that leaves the root through a link inside it.
        Directory.CreateDirectory(MountRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateSymbolicLink(Path.Combine(MountRoot, "out"), real);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.MountAsync(new SealedGitMountRequest(MountRoot, Path.Combine(MountRoot, "out", "vault"), _protected)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
    }

    [Fact]
    public async Task Nested_config_and_hooks_are_never_sealed_but_ref_names_like_them_are()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(async g =>
        {
            var work = Path.Combine(Path.GetDirectoryName(g)!, "wt");
            Directory.CreateDirectory(work);
            await File.WriteAllTextAsync(Path.Combine(work, "note.md"), "one");
            await Git(g, work, "add", "note.md");
            await CommitAll(g, work, "one");
            Directory.CreateDirectory(Path.Combine(g, "modules", "x", "hooks"));
            await File.WriteAllTextAsync(Path.Combine(g, "modules", "x", "config"), "[core]\n\tfsmonitor = evil\n");
            await File.WriteAllTextAsync(Path.Combine(g, "modules", "x", "hooks", "post-checkout"), "#!/bin/sh\n");
            await Git(g, null, "branch", "hooks/config");
            await Git(g, null, "branch", "config");
            return 0;
        });

        var paths = await SealedPaths();
        Assert.DoesNotContain(paths, p => p.EndsWith("/config", StringComparison.Ordinal) && !p.StartsWith("refs/", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains("hooks/post-checkout", StringComparison.Ordinal));
        Assert.Contains("refs/heads/hooks/config", paths);
        Assert.Contains("refs/heads/config", paths);
    }

    [Fact]
    public async Task Object_alternates_are_refused_on_reseal()
    {
        var repo = Repo();
        var before = await SealedPaths();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.WithWorkingTreeAsync(async g =>
        {
            await File.WriteAllTextAsync(Path.Combine(g, "objects", "info", "alternates"), "/elsewhere/objects\n");
            return 0;
        }));
        Assert.Equal(before, await SealedPaths());
    }

    [Fact]
    public async Task A_stale_mount_with_unsealed_commits_is_refused_unless_adopted()
    {
        var repo = Repo();
        var checkout = Path.Combine(MountRoot, "vault");
        var crashed = Path.Combine(_dir, "crashed");
        await using (var mount = await repo.MountAsync(new SealedGitMountRequest(MountRoot, checkout, _protected)))
        {
            await File.WriteAllTextAsync(Path.Combine(checkout, "note.md"), _canary);
            await Git(mount.GitDir, checkout, "add", "note.md");
            await CommitAll(mount.GitDir, checkout, "unsealed");

            // The process dies here: commit made, reseal never ran. Keep what the crash would leave behind.
            CopyTree(checkout, crashed);
        }

        CopyTree(crashed, checkout);
        await File.WriteAllBytesAsync(checkout + ".bazaar-sealed-mount", []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.MountAsync(new SealedGitMountRequest(MountRoot, checkout, _protected)));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.stale_unsealed");
        Assert.Equal(_canary, await File.ReadAllTextAsync(Path.Combine(checkout, "note.md")));

        await using (await repo.MountAsync(new SealedGitMountRequest(MountRoot, checkout, _protected, AdoptStaleMount: true)))
        {
            Assert.Equal(_canary, await File.ReadAllTextAsync(Path.Combine(checkout, "note.md")));
        }

        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.stale_adopted");
        await using (await repo.MountAsync(new SealedGitMountRequest(MountRoot, checkout, _protected)))
        {
            Assert.Equal(_canary, await File.ReadAllTextAsync(Path.Combine(checkout, "note.md")));
        }
    }

    [Fact]
    public async Task A_stale_mount_without_unsealed_refs_is_wiped_and_unknown_markers_are_audited()
    {
        var repo = Repo();
        var checkout = Path.Combine(MountRoot, "vault");
        await using (await repo.MountAsync(new SealedGitMountRequest(MountRoot, checkout, _protected)))
        {
        }

        Directory.CreateDirectory(Path.Combine(checkout, "left"));
        await File.WriteAllTextAsync(Path.Combine(checkout, "left", "x.md"), _canary);
        await File.WriteAllBytesAsync(checkout + ".bazaar-sealed-mount", []);
        await File.WriteAllBytesAsync(Path.Combine(MountRoot, "other.bazaar-sealed-mount"), []);

        await using (await repo.MountAsync(new SealedGitMountRequest(MountRoot, checkout, _protected)))
        {
            Assert.False(Directory.Exists(Path.Combine(checkout, "left")));
        }

        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.mount.stale_wiped");
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.mount.unknown_marker");
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.unmount" && e.Detail.Contains("wiped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_nested_checkout_gets_owner_only_parents()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repo = Repo();
        var checkout = Path.Combine(MountRoot, "a", "b", "vault");
        await using (await repo.MountAsync(new SealedGitMountRequest(MountRoot, checkout, _protected)))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(MountRoot, "a")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(MountRoot, "a", "b")));
        }
    }

    [Fact]
    public async Task A_broken_head_fails_the_mount_instead_of_looking_unborn()
    {
        var repo = Repo();
        using (var scope = await NewStore().OpenScopeAsync("vault:acme"))
        {
            await scope.WriteAsync("HEAD", Encoding.ASCII.GetBytes("ref: refs/heads/main\n"));
            await scope.WriteAsync("refs/heads/main", Encoding.ASCII.GetBytes(new string('d', 40) + "\n"));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.MountAsync(new SealedGitMountRequest(MountRoot, Path.Combine(MountRoot, "vault"), _protected)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(MountRoot));
    }

    [Fact]
    public async Task Import_verifies_under_the_mount_root_and_a_head_mismatch_rolls_back()
    {
        var source = await PlainRepoAsync("plain", _canary);
        var repo = Repo();
        string? verifyDir = null;
        repo.AfterImportSealedForTest = async g =>
        {
            verifyDir = g;
            await File.WriteAllTextAsync(Path.Combine(g, "refs", "heads", "main"), new string('b', 40) + "\n");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.ImportCheckoutAsync(source, MountRoot, _protected));
        Assert.NotNull(verifyDir);
        Assert.StartsWith(MountRoot + Path.DirectorySeparatorChar, verifyDir, StringComparison.Ordinal);
        Assert.Empty(await SealedPaths());
        Assert.Equal(_canary, await File.ReadAllTextAsync(Path.Combine(source, "note.md")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(MountRoot));
    }

    [Fact]
    public async Task An_fsck_failure_rolls_the_import_back()
    {
        var source = await PlainRepoAsync("plain", "x");
        var garbage = Path.Combine(source, ".git", "objects", "ab");
        Directory.CreateDirectory(garbage);
        await File.WriteAllTextAsync(Path.Combine(garbage, new string('c', 38)), "not a zlib object");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Repo().ImportCheckoutAsync(source, MountRoot, _protected));
        Assert.Empty(await SealedPaths());
        Assert.True(Directory.Exists(Path.Combine(source, ".git")));
    }

    [Fact]
    public async Task A_second_copy_with_the_same_head_finishes_the_import_without_sealing_again()
    {
        var source = await PlainRepoAsync("plain", _canary);
        var copy = Path.Combine(_dir, "copy");
        CopyTree(source, copy);
        var repo = Repo();
        Assert.True(await repo.ImportCheckoutAsync(source, MountRoot, _protected) > 0);
        Assert.False(Directory.Exists(source));
        var sealedBefore = await SealedPaths();

        Assert.Equal(0, await repo.ImportCheckoutAsync(copy, MountRoot, _protected));
        Assert.False(Directory.Exists(copy));
        Assert.Equal(sealedBefore, await SealedPaths());
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.import.already_done");
    }

    [Theory]
    [InlineData("ignored", "untracked or ignored")]
    [InlineData("untracked", "untracked or ignored")]
    [InlineData("gitmodules", "submodules")]
    [InlineData("modules", "submodules")]
    [InlineData("gitlink", "gitlink")]
    [InlineData("alternates", "alternates")]
    [InlineData("detached", "detached")]
    [InlineData("rebase", "rebase-merge")]
    [InlineData("bisect", "bisect")]
    [InlineData("stash", "stash")]
    public async Task Import_refuses_sources_it_cannot_seal_whole(string kind, string reason)
    {
        var source = await PlainRepoAsync("plain", "x");
        var gitDir = Path.Combine(source, ".git");
        switch (kind)
        {
            case "ignored":
                await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "*.log\n");
                await Git(gitDir, source, "add", ".gitignore");
                await CommitAll(gitDir, source, "ignore");
                await File.WriteAllTextAsync(Path.Combine(source, "debug.log"), "ignored but would be wiped");
                break;
            case "untracked":
                await File.WriteAllTextAsync(Path.Combine(source, "draft.md"), "not committed");
                break;
            case "gitmodules":
                await File.WriteAllTextAsync(Path.Combine(source, ".gitmodules"), "[submodule \"x\"]\n\tpath = x\n\turl = ../x\n");
                break;
            case "modules":
                Directory.CreateDirectory(Path.Combine(gitDir, "modules", "x"));
                break;
            case "gitlink":
                await Git(gitDir, source, "update-index", "--add", "--cacheinfo", "160000," + new string('e', 40) + ",sub");
                await CommitAll(gitDir, source, "gitlink");
                break;
            case "alternates":
                await File.WriteAllTextAsync(Path.Combine(gitDir, "objects", "info", "alternates"), "/elsewhere/objects\n");
                break;
            case "detached":
                var head = (await GitCommand.RunAsync(gitDir, ["rev-parse", "HEAD"])).StandardOutput.Trim();
                await File.WriteAllTextAsync(Path.Combine(gitDir, "HEAD"), head + "\n");
                break;
            case "rebase":
                Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"));
                break;
            case "bisect":
                await File.WriteAllTextAsync(Path.Combine(gitDir, "BISECT_START"), "main\n");
                break;
            case "stash":
                Directory.CreateDirectory(Path.Combine(gitDir, "logs", "refs"));
                await File.WriteAllTextAsync(Path.Combine(gitDir, "logs", "refs", "stash"), "a b c 1 +0000\tone\nd e f 2 +0000\ttwo\n");
                break;
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Repo().ImportCheckoutAsync(source, MountRoot, _protected));
        Assert.Contains(reason, refused.Message, StringComparison.Ordinal);
        Assert.Empty(await SealedPaths());
        Assert.True(Directory.Exists(gitDir));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.import.failed");
    }

    private sealed class FixedVerifier(bool isProtected) : IMountRootVerifier
    {
        public MountRootVerdict Verify(string mountRoot) => new(isProtected, isProtected ? "test" : "test: not protected");
    }

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
