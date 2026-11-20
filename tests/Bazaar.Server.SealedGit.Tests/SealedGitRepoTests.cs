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

    private ISealedGitRepo Repo()
    {
        Directory.CreateDirectory(Temp);
        var store = new SealedStore(new SealedStoreOptions { RootPath = SealedRoot }, FileKekProvider.Open(Path.Combine(_dir, "kek.key")), _anchor, _audit);
        return new SealedGitRepoFactory(store, _audit, new SealedGitOptions { TempRoot = Temp }).Open("acme");
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
