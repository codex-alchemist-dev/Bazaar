using System.Text;
using Bazaar.Server.Identity.Kek;
using Bazaar.Server.SealedStorage;

namespace Bazaar.Server.SealedGit.Tests;

public sealed class SealedGitRepoTests : IDisposable
{
    private const string _canary = "CANARY-vault-note-do-not-leak-4417";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bazaar-sealedgit-test-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _audit = new();
    private readonly InMemorySealAnchor _anchor = new();

    public SealedGitRepoTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Temp => Path.Combine(_dir, "tmp");

    private string SealedRoot => Path.Combine(_dir, "sealed");

    private SealedStore NewStore() => new(new SealedStoreOptions { RootPath = SealedRoot }, FileKekProvider.Open(Path.Combine(_dir, "kek.key")), _anchor, _audit);

    private ISealedGitRepo Repo(SealedGitOptions? options = null)
    {
        Directory.CreateDirectory(Temp);
        return new SealedGitRepoFactory(NewStore(), _audit, options ?? new SealedGitOptions { TempRoot = Temp }).Open("acme");
    }

    private async Task<string[]> SealedPaths()
    {
        using var scope = await NewStore().OpenScopeAsync("vault:acme");
        return (await scope.ListAsync()).Select(e => e.Path).ToArray();
    }

    private static async Task<string> Git(string gitDir, params string[] args)
    {
        var r = await GitCommand.RunAsync(gitDir, args);
        Assert.True(r.ExitCode == 0, $"git {string.Join(' ', args)} failed: {r.StandardError}");
        return r.StandardOutput;
    }

    private static Task<bool> Commit(string gitDir, string text) => CommitAsync(gitDir, text);

    private static async Task<bool> CommitAsync(string gitDir, string text)
    {
        var work = Path.Combine(Path.GetDirectoryName(gitDir)!, "wt");
        Directory.CreateDirectory(work);
        await File.WriteAllTextAsync(Path.Combine(work, "note.md"), text);
        var r = await GitCommand.RunAsync(gitDir, ["add", "note.md"], work);
        Assert.Equal(0, r.ExitCode);
        r = await GitCommand.RunAsync(gitDir, ["-c", "user.name=T", "-c", "user.email=t@example.invalid", "commit", "-q", "-m", "note"], work);
        Assert.True(r.ExitCode == 0, r.StandardError);
        return true;
    }

    [Fact]
    public async Task A_commit_survives_seal_and_unseal()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, _canary));

        var text = await repo.WithWorkingTreeAsync(g => Git(g, "show", "HEAD:note.md"));
        Assert.Equal(_canary, text);
        var count = await repo.WithWorkingTreeAsync(g => Git(g, "rev-list", "--count", "HEAD"));
        Assert.Equal("1", count.Trim());
    }

    [Fact]
    public async Task A_second_commit_chains_onto_the_first()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, "one"));
        await repo.WithWorkingTreeAsync(g => Commit(g, "two"));
        var count = await repo.WithWorkingTreeAsync(g => Git(g, "rev-list", "--count", "HEAD"));
        Assert.Equal("2", count.Trim());
    }

    [Fact]
    public async Task No_plaintext_blob_or_commit_text_is_left_on_disk()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, _canary));

        Assert.Empty(Directory.EnumerateFileSystemEntries(Temp));
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("kek.key", StringComparison.Ordinal)))
        {
            var bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain(_canary, Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
            Assert.DoesNotContain("note", Path.GetFileName(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_tampered_sealed_file_fails_closed()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, _canary));

        foreach (var file in Directory.GetFiles(SealedRoot, "*.bzs", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            bytes[^20] ^= 0x01;
            File.WriteAllBytes(file, bytes);
        }

        await Assert.ThrowsAsync<SealTamperedException>(() => repo.WithWorkingTreeAsync(g => Task.FromResult(0)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Temp));
    }

    [Fact]
    public async Task The_temp_dir_is_wiped_when_the_callback_throws_and_nothing_is_sealed()
    {
        var repo = Repo();
        string? seen = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.WithWorkingTreeAsync<int>(async g =>
        {
            seen = g;
            await Commit(g, _canary);
            throw new InvalidOperationException("boom");
        }));

        Assert.NotNull(seen);
        Assert.False(Directory.Exists(seen));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Temp));

        var heads = await repo.WithWorkingTreeAsync(g => Git(g, "for-each-ref"));
        Assert.Equal(string.Empty, heads);
    }

    [Fact]
    public async Task Export_writes_a_usable_repo_and_is_audited()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, _canary));

        var target = Path.Combine(_dir, "export");
        var files = await repo.ExportAsync(target);
        Assert.True(files > 0);
        Assert.Equal(_canary, await Git(target, "show", "HEAD:note.md"));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.export" && e.Scope == "vault:acme");

        await Assert.ThrowsAsync<IOException>(() => repo.ExportAsync(target));
    }

    [Fact]
    public async Task Restore_after_gc_recreates_ref_and_object_dirs()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, "one"));
        await repo.WithWorkingTreeAsync(async g =>
        {
            await Git(g, "gc", "-q");
            return 0;
        });
        await repo.WithWorkingTreeAsync(g => Commit(g, "two"));
        Assert.Equal("2", (await repo.WithWorkingTreeAsync(g => Git(g, "rev-list", "--count", "HEAD"))).Trim());
    }

    [Fact]
    public async Task An_oversized_file_fails_before_any_write()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, "one"));
        var before = await SealedPaths();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.WithWorkingTreeAsync(async g =>
        {
            await Commit(g, "two");
            using var fs = File.Create(Path.Combine(g, "objects", "info", "huge"));
            fs.SetLength(257L * 1024 * 1024);
            return 0;
        }));
        Assert.Equal(before, await SealedPaths());
        Assert.Equal("1", (await repo.WithWorkingTreeAsync(g => Git(g, "rev-list", "--count", "HEAD"))).Trim());
    }

    [Fact]
    public async Task A_failure_part_way_leaves_the_old_refs_valid()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, "one"));
        await Assert.ThrowsAsync<SealGenerationConflictException>(() => repo.WithWorkingTreeAsync(async g =>
        {
            await Commit(g, "two");
            using var other = await NewStore().OpenScopeAsync("vault:acme");
            await other.WriteAsync("refs/heads/main", Encoding.ASCII.GetBytes(new string('a', 40) + "\n"));
            return 0;
        }));
        using var scope = await NewStore().OpenScopeAsync("vault:acme");
        var sealedRef = Encoding.ASCII.GetString((await scope.ReadAsync("refs/heads/main"))!.Content);
        Assert.StartsWith(new string('a', 40), sealedRef, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Links_are_refused_and_never_followed()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outside = Path.Combine(_dir, "outside.txt");
        await File.WriteAllTextAsync(outside, _canary);
        var repo = Repo();
        await Assert.ThrowsAsync<InvalidOperationException>(() => repo.WithWorkingTreeAsync(g =>
        {
            File.CreateSymbolicLink(Path.Combine(g, "evil"), outside);
            return Task.FromResult(0);
        }));
        Assert.Equal(_canary, await File.ReadAllTextAsync(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Temp));
    }

    [Fact]
    public async Task Hard_links_are_not_zeroed_on_wipe()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var outside = Path.Combine(_dir, "outside.txt");
        await File.WriteAllTextAsync(outside, _canary);
        var repo = Repo();
        await repo.WithWorkingTreeAsync(async g =>
        {
            var ln = await RunAsync("ln", outside, Path.Combine(g, "info-extra"));
            Assert.Equal(0, ln);
            return 0;
        });
        Assert.Equal(_canary, await File.ReadAllTextAsync(outside));
    }

    private static async Task<int> RunAsync(string file, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = System.Diagnostics.Process.Start(psi)!;
        await p.WaitForExitAsync();
        return p.ExitCode;
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Hooks_and_hostile_config_are_not_sealed_or_run()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var marker = Path.Combine(_dir, "hook-ran");
        var repo = Repo();
        await repo.WithWorkingTreeAsync(async g =>
        {
            Directory.CreateDirectory(Path.Combine(g, "hooks"));
            var hook = Path.Combine(g, "hooks", "pre-commit");
            await File.WriteAllTextAsync(hook, $"#!/bin/sh\ntouch '{marker}'\n");
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await File.AppendAllTextAsync(Path.Combine(g, "config"), "[core]\n\tsshCommand = touch evil\n[alias]\n\tx = !touch evil\n");
            return await Commit(g, "one");
        });

        Assert.False(File.Exists(marker));
        Assert.DoesNotContain(await SealedPaths(), p => p.StartsWith("hooks/", StringComparison.Ordinal));
        using var scope = await NewStore().OpenScopeAsync("vault:acme");
        var config = Encoding.UTF8.GetString((await scope.ReadAsync("config"))?.Content ?? []);
        Assert.DoesNotContain("alias", config, StringComparison.Ordinal);
        Assert.DoesNotContain("sshCommand", config, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_allowlist_accepts_the_default_and_rejects_others()
    {
        Assert.True(SealedGitRepo.ConfigIsSafe("[core]\n\trepositoryformatversion = 0\n\tbare = true\n[user]\n\tname = a\n"));
        Assert.False(SealedGitRepo.ConfigIsSafe("[core]\n\tfsmonitor = x\n"));
        Assert.False(SealedGitRepo.ConfigIsSafe("[remote \"o\"]\n\turl = x\n"));
    }

    [Fact]
    public async Task Stale_temp_dirs_are_swept_when_the_factory_is_created()
    {
        var stale = Path.Combine(Temp, "bazaar-sealedgit-stale");
        Directory.CreateDirectory(stale);
        await File.WriteAllTextAsync(Path.Combine(stale, "x"), _canary);
        _ = Repo();
        Assert.False(Directory.Exists(stale));
    }

    [Fact]
    public async Task A_default_temp_root_is_audited_as_a_warning()
    {
        var repo = Repo(new SealedGitOptions());
        await repo.WithWorkingTreeAsync(g => Task.FromResult(0));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.temp_root_default");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task The_temp_dir_is_owner_only()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repo = Repo();
        UnixFileMode mode = default;
        await repo.WithWorkingTreeAsync(g =>
        {
            mode = File.GetUnixFileMode(Path.GetDirectoryName(g)!);
            return Task.FromResult(0);
        });
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, mode);
    }

    [Fact]
    public async Task Git_child_does_not_inherit_git_variables()
    {
        Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", "touch evil");
        try
        {
            var repo = Repo();
            var env = await repo.WithWorkingTreeAsync(async g => (await GitCommand.RunAsync(g, ["var", "GIT_EDITOR"])).ExitCode);
            Assert.Equal(0, env);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", null);
        }
    }

    [Fact]
    public async Task Export_refuses_link_targets_and_audits_start_and_failure()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, "one"));
        var real = Path.Combine(_dir, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, real);
        await Assert.ThrowsAsync<IOException>(() => repo.ExportAsync(link));
        Assert.Empty(Directory.EnumerateFileSystemEntries(real));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.export.start" && e.Detail.Contains(link, StringComparison.Ordinal));
        Assert.Contains(_audit.Events, e => e.Kind == "sealgit.export.failed");
    }

    [Fact]
    public async Task A_concurrent_writer_during_restore_never_yields_new_refs_with_old_packs()
    {
        var repo = Repo();
        await repo.WithWorkingTreeAsync(g => Commit(g, "one"));
        using var writer = await NewStore().OpenScopeAsync("vault:acme");
        using var stop = new CancellationTokenSource();
        var writes = Task.Run(async () =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                await writer.WriteBatchAsync([SealedWrite.Put("objects/pack/race.pack", Encoding.ASCII.GetBytes($"{i}")), SealedWrite.Put("refs/heads/race", Encoding.ASCII.GetBytes($"{i}"))]);
                await Task.Delay(2);
            }
        });

        for (var n = 0; n < 15; n++)
        {
            await repo.WithWorkingTreeAsync(async g =>
            {
                var pack = Path.Combine(g, "objects", "pack", "race.pack");
                var reference = Path.Combine(g, "refs", "heads", "race");
                if (File.Exists(pack) || File.Exists(reference))
                {
                    Assert.Equal(await File.ReadAllTextAsync(pack), await File.ReadAllTextAsync(reference));
                }

                return 0;
            });
        }

        await stop.CancelAsync();
        await writes;
    }

    [Fact]
    public async Task The_lease_makes_a_whole_cycle_exclusive_against_a_second_store()
    {
        var first = Repo();
        var second = new SealedGitRepoFactory(NewStore(), _audit, new SealedGitOptions { TempRoot = Temp, LeaseTimeout = TimeSpan.FromMilliseconds(300) }).Open("acme");
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holding = first.WithWorkingTreeAsync(async g =>
        {
            entered.SetResult();
            await release.Task;
            return await Commit(g, "one");
        });
        await entered.Task;
        await Assert.ThrowsAsync<TimeoutException>(() => second.WithWorkingTreeAsync(g => Commit(g, "two")));
        release.SetResult();
        await holding;
        await second.WithWorkingTreeAsync(g => Commit(g, "two"));
        Assert.Equal("2", (await first.WithWorkingTreeAsync(g => Git(g, "rev-list", "--count", "HEAD"))).Trim());
    }

    private sealed class RecordingSink : ISealAuditSink
    {
        public List<SealAuditEvent> Events { get; } = [];

        public ValueTask RecordAsync(SealAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add(auditEvent);
            }

            return ValueTask.CompletedTask;
        }
    }
}
