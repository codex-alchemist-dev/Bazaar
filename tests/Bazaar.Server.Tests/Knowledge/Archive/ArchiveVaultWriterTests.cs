using Bazaar.Server.Knowledge.Archive;
using Bazaar.Server.Memory.Trust;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Knowledge.Archive;

/// <summary>Chapter write through <see cref="VaultWriter"/> against a real temp git repository (no Docker).</summary>
public sealed class ArchiveVaultWriterTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _end = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TempGitRepository _repo = new(("README.md", "vault\n"));
    private readonly LibGit2GitProvider _git = new();
    private readonly VaultWriter _writer;
    private readonly string _worktreesRoot = Path.Combine(Path.GetTempPath(), "bazaar-archive-wt-" + Guid.NewGuid().ToString("N"));
    private readonly ArchiveVaultWriter _archive;

    public ArchiveVaultWriterTests()
    {
        _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer);
        _archive = new ArchiveVaultWriter(_git, _writer, _repo.Checkout, _worktreesRoot);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
        if (Directory.Exists(_worktreesRoot))
        {
            Directory.Delete(_worktreesRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Chapter_lands_on_main_under_the_archive_identity_with_its_front_matter()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        var chapter = Chapter("2026-11", predecessor: null);

        var result = await _archive.WriteAsync(chapter, baseCommit, CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Merged, result.Outcome);
        var text = File.ReadAllText(Path.Combine(_repo.Checkout, "archive", "bazaar", "2026-11.md"));
        Assert.Contains("type: archive\n", text);
        Assert.Contains("trust: T2\n", text);
        Assert.Contains("origin_kind: agent\n", text);
        Assert.Contains("Alpha said. <!-- source: gc_message:a -->", text);

        using var repo = new Repository(_repo.Checkout);
        var message = repo.Lookup<Commit>(result.NewCommit).Message;
        Assert.Contains("Bazaar-Agent: bazaar-archive", message);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
    }

    [Fact]
    public async Task Regenerated_chapter_links_its_predecessor_and_both_versions_stay()
    {
        var first = await _archive.WriteAsync(Chapter("2026-11", predecessor: null), TempGitRepository.HeadOf(_repo.Checkout), CancellationToken.None);

        var second = await _archive.WriteAsync(
            Chapter("2026-11-r2", predecessor: "archive/bazaar/2026-11.md"),
            first.NewCommit,
            CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Merged, second.Outcome);
        Assert.True(File.Exists(Path.Combine(_repo.Checkout, "archive", "bazaar", "2026-11.md")));
        var regenerated = File.ReadAllText(Path.Combine(_repo.Checkout, "archive", "bazaar", "2026-11-r2.md"));
        Assert.Contains("predecessor: archive/bazaar/2026-11.md\n", regenerated);
    }

    [Fact]
    public async Task Existing_chapter_path_is_never_overwritten()
    {
        var first = await _archive.WriteAsync(Chapter("2026-11", predecessor: null), TempGitRepository.HeadOf(_repo.Checkout), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _archive.WriteAsync(Chapter("2026-11", predecessor: null), first.NewCommit, CancellationToken.None));
    }

    [Fact]
    public async Task Predecessor_must_exist_at_the_base_commit()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _archive.WriteAsync(
                Chapter("2026-11-r2", predecessor: "archive/bazaar/2026-11.md"),
                TempGitRepository.HeadOf(_repo.Checkout),
                CancellationToken.None));
    }

    [Fact]
    public async Task Chapter_worktree_is_removed_after_the_write()
    {
        await _archive.WriteAsync(Chapter("2026-11", predecessor: null), TempGitRepository.HeadOf(_repo.Checkout), CancellationToken.None);

        Assert.Empty(Directory.Exists(_worktreesRoot) ? Directory.EnumerateDirectories(_worktreesRoot) : []);
    }

    private static ValidatedArchiveChapter Chapter(string slug, string? predecessor)
    {
        var sources = new[]
        {
            new ArchiveSource(
                Ref: ArchiveSourceRef.GcMessage("a"),
                Kind: ArchiveSourceKind.GcMessage,
                TextClass: ArchiveTextClass.Agent,
                GcOptedIn: false,
                JointRevealDone: false,
                At: new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero),
                Text: "Alpha said.",
                Trust: MemoryTier.AgentVerified,
                OriginKind: ContentTrust.Agent),
        };
        var draft = ArchiveChapterGenerator.Generate(new ArchiveChapterRequest(
            Guid.Parse("0196a7c4-0000-7000-8000-0000000000c1"),
            "bazaar",
            slug,
            "Bazaar " + slug,
            _start,
            _end,
            sources,
            predecessor));
        return ArchiveChapterValidator.Validate(draft, sources, ArchiveRules.Defaults);
    }
}
