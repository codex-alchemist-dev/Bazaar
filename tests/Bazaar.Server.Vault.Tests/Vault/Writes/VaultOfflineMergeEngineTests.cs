using System.Text;
using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>The offline push merge (doc 18, "Offline merge"; doc 49 section 4 item 2).</summary>
public sealed class VaultOfflineMergeEngineTests : IDisposable
{
    private const string _device = "phone-1";
    private const string _stamp = "20261118T101500Z";

    private static readonly string _baseDoc = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n";
    private static readonly byte[] _baseBinary = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03, 0x00, 0xFF];

    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();
    private readonly RecordingObserver _observer = new();
    private readonly VaultWriter _writer;
    private readonly VaultOfflineMergeEngine _engine;
    private readonly string _base;

    public VaultOfflineMergeEngineTests()
    {
        _repo = new TempGitRepository(("doc.md", _baseDoc), ("other.md", _baseDoc));
        WriteBinary(_repo.Checkout, "img.bin", _baseBinary);
        TempGitRepository.CommitAll(_repo.Checkout, "binary");
        _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer, _observer);
        _engine = new VaultOfflineMergeEngine(_writer, _repo.WorktreePath("offline"), new FixedTime(new DateTimeOffset(2026, 11, 18, 10, 15, 0, TimeSpan.Zero)));
        _base = TempGitRepository.HeadOf(_repo.Checkout);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task A_push_onto_an_unmoved_main_fast_forwards()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));

        var result = await MergeAsync(head);

        Assert.Equal(new VaultOfflineMergeResult.Merged(head), result);
        Assert.Equal(head, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Contains("device 3", TempGitRepository.ReadFile(_repo.Checkout, "doc.md"), StringComparison.Ordinal);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.Equal([(_base, head)], _observer.Commits);
    }

    [Fact]
    public async Task Disjoint_hunks_in_one_file_merge_into_main_with_trailers()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 9, "device 9")));
        var oldMain = MainCommit("doc.md", Edit(_baseDoc, 2, "main 2"));

        var merged = Assert.IsType<VaultOfflineMergeResult.Merged>(await MergeAsync(head));

        Assert.Equal(merged.NewHead, TempGitRepository.HeadOf(_repo.Checkout));
        var text = TempGitRepository.ReadFile(_repo.Checkout, "doc.md");
        Assert.Contains("main 2", text, StringComparison.Ordinal);
        Assert.Contains("device 9", text, StringComparison.Ordinal);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));

        using (var repo = new Repository(_repo.Checkout))
        {
            var commit = repo.Lookup<Commit>(merged.NewHead);
            Assert.Equal([oldMain, head], commit.Parents.Select(p => p.Sha));
            Assert.Contains($"Bazaar-Device: {_device}", commit.Message, StringComparison.Ordinal);
            Assert.Contains($"Bazaar-Edit-Base: {_base}", commit.Message, StringComparison.Ordinal);
            Assert.Equal(TempGitRepository.Writer.Name, commit.Committer.Name);
        }

        Assert.Equal([(oldMain, merged.NewHead)], _observer.Commits);
        await AssertNoScratchLeftAsync();
    }

    [Fact]
    public async Task Overlapping_hunks_go_to_a_conflict_branch_and_leave_main_untouched()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 5, "device 5")));
        var oldMain = MainCommit("doc.md", Edit(_baseDoc, 5, "main 5"));

        var result = await MergeAsync(head);

        var conflicted = Assert.IsType<VaultOfflineMergeResult.Conflicted>(result);
        Assert.Equal($"conflict/{_device}/{_stamp}", conflicted.BranchName);
        Assert.Equal(["doc.md"], conflicted.ConflictedPaths);
        Assert.Equal(head, TempGitRepository.TipOf(_repo.Checkout, conflicted.BranchName));
        Assert.Equal(oldMain, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Contains("main 5", TempGitRepository.ReadFile(_repo.Checkout, "doc.md"), StringComparison.Ordinal);
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.Empty(_observer.Commits);
        await AssertNoScratchLeftAsync();
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);
    }

    [Fact]
    public async Task Repeating_a_conflicting_push_reuses_its_branch_and_a_new_head_gets_a_suffix()
    {
        MainCommit("doc.md", Edit(_baseDoc, 5, "main 5"));
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 5, "device 5")));
        var other = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 5, "device 5 again")));

        var first = Assert.IsType<VaultOfflineMergeResult.Conflicted>(await MergeAsync(head));
        var repeat = Assert.IsType<VaultOfflineMergeResult.Conflicted>(await MergeAsync(head));
        var second = Assert.IsType<VaultOfflineMergeResult.Conflicted>(await MergeAsync(other));

        Assert.Equal(first.BranchName, repeat.BranchName);
        Assert.Equal($"conflict/{_device}/{_stamp}-2", second.BranchName);
        Assert.Equal(other, TempGitRepository.TipOf(_repo.Checkout, second.BranchName));
    }

    [Fact]
    public async Task Add_add_with_different_content_conflicts()
    {
        var head = DeviceCommit(_base, ("new.md", "device new\n"));
        MainCommit("new.md", "main new\n");

        var conflicted = Assert.IsType<VaultOfflineMergeResult.Conflicted>(await MergeAsync(head));

        Assert.Equal(["new.md"], conflicted.ConflictedPaths);
    }

    [Fact]
    public async Task Add_add_with_identical_content_merges()
    {
        var head = DeviceCommit(_base, ("new.md", "same\n"), ("doc.md", Edit(_baseDoc, 9, "device 9")));
        MainCommit("new.md", "same\n");

        Assert.IsType<VaultOfflineMergeResult.Merged>(await MergeAsync(head));

        Assert.Equal("same\n", TempGitRepository.ReadFile(_repo.Checkout, "new.md"));
        Assert.Contains("device 9", TempGitRepository.ReadFile(_repo.Checkout, "doc.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_on_the_device_and_modify_on_main_conflicts()
    {
        var head = DeviceCommit(_base, ("other.md", null));
        MainCommit("other.md", Edit(_baseDoc, 4, "main 4"));

        var conflicted = Assert.IsType<VaultOfflineMergeResult.Conflicted>(await MergeAsync(head));

        Assert.Equal(["other.md"], conflicted.ConflictedPaths);
        Assert.True(File.Exists(Path.Combine(_repo.Checkout, "other.md")));
    }

    [Fact]
    public async Task A_binary_file_changed_on_both_sides_conflicts_as_a_whole_file()
    {
        var head = DeviceCommitBytes(_base, "img.bin", [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03, 0x00, 0x11]);
        WriteBinary(_repo.Checkout, "img.bin", [0x22, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02, 0x03, 0x00, 0xFF]);
        var oldMain = TempGitRepository.CommitAll(_repo.Checkout, "main binary");

        var conflicted = Assert.IsType<VaultOfflineMergeResult.Conflicted>(await MergeAsync(head));

        Assert.Equal(["img.bin"], conflicted.ConflictedPaths);
        Assert.Equal(oldMain, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task A_rename_on_the_device_keeps_main_edits_to_the_renamed_file()
    {
        var head = DeviceCommit(_base, ("other.md", null), ("moved/other.md", _baseDoc));
        MainCommit("other.md", Edit(_baseDoc, 1, "main 1"));

        Assert.IsType<VaultOfflineMergeResult.Merged>(await MergeAsync(head));

        Assert.False(File.Exists(Path.Combine(_repo.Checkout, "other.md")));
        Assert.StartsWith("main 1\n", TempGitRepository.ReadFile(_repo.Checkout, "moved/other.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_that_is_not_an_ancestor_of_head_is_rejected()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));
        var laterMain = MainCommit("other.md", Edit(_baseDoc, 1, "main 1"));

        var result = await MergeAsync(head, laterMain);

        Assert.Equal("base is not an ancestor of head", Assert.IsType<VaultOfflineMergeResult.Rejected>(result).Reason);
        Assert.Equal(laterMain, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task A_base_that_is_not_on_main_is_rejected()
    {
        var offMain = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));
        var head = DeviceCommit(offMain, ("doc.md", Edit(Edit(_baseDoc, 3, "device 3"), 4, "device 4")));

        var result = await MergeAsync(head, offMain);

        Assert.Equal("base is not on branch 'main'", Assert.IsType<VaultOfflineMergeResult.Rejected>(result).Reason);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000", true)]
    [InlineData("abc", false)]
    [InlineData("main", false)]
    public async Task Unknown_or_malformed_commits_are_rejected(string head, bool wellFormed)
    {
        var result = Assert.IsType<VaultOfflineMergeResult.Rejected>(await MergeAsync(head));

        Assert.Equal(wellFormed ? $"unknown head commit {head}" : "head and base must be full commit SHAs", result.Reason);
    }

    [Fact]
    public async Task A_non_main_source_branch_merges_into_main_and_is_recorded()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 9, "device 9")));
        var oldMain = MainCommit("doc.md", Edit(_baseDoc, 2, "main 2"));

        var result = await _engine.MergeAsync(
            new VaultOfflineMergeRequest(_device, "refs/heads/offline/phone-1/drafts", head, _base, "0196a7c4-0000-7000-8000-0000000000aa"),
            CancellationToken.None);

        var merged = Assert.IsType<VaultOfflineMergeResult.Merged>(result);
        using var repo = new Repository(_repo.Checkout);
        Assert.Equal("main", repo.Head.FriendlyName);
        var commit = repo.Lookup<Commit>(merged.NewHead);
        Assert.Equal([oldMain, head], commit.Parents.Select(p => p.Sha));
        Assert.Contains("Bazaar-Offline-Branch: offline/phone-1/drafts", commit.Message, StringComparison.Ordinal);
        Assert.Contains("Bazaar-Offline-Write: 0196a7c4-0000-7000-8000-0000000000aa", commit.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_source_branch_named_main_is_still_only_the_source()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));

        // The device pushes its local "main". The server's main does not contain the head yet; that must not block it.
        var result = await _engine.MergeAsync(new VaultOfflineMergeRequest(_device, "refs/heads/main", head, _base), CancellationToken.None);

        Assert.Equal(new VaultOfflineMergeResult.Merged(head), result);
    }

    [Fact]
    public async Task An_existing_source_branch_must_contain_the_head()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));
        var elsewhere = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 4, "device 4")));
        using (var repo = new Repository(_repo.Checkout))
        {
            repo.CreateBranch("offline/phone-1", repo.Lookup<Commit>(elsewhere));
        }

        var result = await _engine.MergeAsync(new VaultOfflineMergeRequest(_device, "offline/phone-1", head, _base), CancellationToken.None);
        var onBranch = await _engine.MergeAsync(new VaultOfflineMergeRequest(_device, "offline/phone-1", elsewhere, _base), CancellationToken.None);

        Assert.Equal("head is not reachable from source branch 'offline/phone-1'", Assert.IsType<VaultOfflineMergeResult.Rejected>(result).Reason);
        Assert.Equal(new VaultOfflineMergeResult.Merged(elsewhere), onBranch);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("refs/heads/")]
    [InlineData("has space")]
    [InlineData("line\nbreak")]
    [InlineData("../escape")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData(".git/x")]
    [InlineData("x.lock")]
    [InlineData("a..b")]
    [InlineData("tilde~1")]
    [InlineData("caret^")]
    [InlineData("colon:x")]
    [InlineData("star*")]
    [InlineData("back\\slash")]
    [InlineData("trailing/")]
    [InlineData("@{-1}")]
    public async Task Odd_source_branch_names_are_rejected_without_touching_main(string branch)
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));

        var result = await _engine.MergeAsync(new VaultOfflineMergeRequest(_device, branch, head, _base), CancellationToken.None);

        Assert.Equal("source branch is not a valid branch name", Assert.IsType<VaultOfflineMergeResult.Rejected>(result).Reason);
        Assert.Equal(_base, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task A_very_long_source_branch_name_is_rejected()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));

        var result = await _engine.MergeAsync(new VaultOfflineMergeRequest(_device, new string('b', 201), head, _base), CancellationToken.None);

        Assert.IsType<VaultOfflineMergeResult.Rejected>(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("new\nline")]
    public async Task A_malformed_write_id_is_rejected(string writeId)
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 3, "device 3")));

        var result = await _engine.MergeAsync(new VaultOfflineMergeRequest(_device, "drafts", head, _base, writeId), CancellationToken.None);

        Assert.Equal("write id is empty, too long or contains whitespace", Assert.IsType<VaultOfflineMergeResult.Rejected>(result).Reason);
    }

    [Fact]
    public async Task Repeating_a_merged_push_returns_the_current_head_and_changes_nothing()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 9, "device 9")));
        MainCommit("doc.md", Edit(_baseDoc, 2, "main 2"));

        var first = Assert.IsType<VaultOfflineMergeResult.Merged>(await MergeAsync(head));
        var repeat = await MergeAsync(head);

        Assert.Equal(first, repeat);
        Assert.Equal(first.NewHead, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Single(_observer.Commits);
    }

    [Fact]
    public async Task A_device_id_is_sanitised_into_one_branch_segment()
    {
        var head = DeviceCommit(_base, ("doc.md", Edit(_baseDoc, 5, "device 5")));
        MainCommit("doc.md", Edit(_baseDoc, 5, "main 5"));

        var result = await _engine.MergeAsync(
            new VaultOfflineMergeRequest("../Ana's Phone/..", "main", head, _base), CancellationToken.None);

        Assert.Equal($"conflict/Ana-s-Phone/{_stamp}", Assert.IsType<VaultOfflineMergeResult.Conflicted>(result).BranchName);
    }

    [Theory]
    [InlineData("phone-1", "phone-1")]
    [InlineData("Ana's Phone", "Ana-s-Phone")]
    [InlineData("../../etc", "etc")]
    [InlineData("a..b", "a.b")]
    [InlineData("tablet.lock", "tablet")]
    [InlineData("-.x", "x")]
    [InlineData("...", "device")]
    [InlineData("名前", "device")]
    public void SanitizeDevice_keeps_a_safe_segment(string deviceId, string expected) =>
        Assert.Equal(expected, VaultOfflineMergeEngine.SanitizeDevice(deviceId));

    [Fact]
    public void SanitizeDevice_caps_the_length() =>
        Assert.Equal(64, VaultOfflineMergeEngine.SanitizeDevice(new string('a', 200)).Length);

    [Theory]
    [InlineData("notes/a.md", true)]
    [InlineData("img.bin", true)]
    [InlineData("../a.md", false)]
    [InlineData("a/../b.md", false)]
    [InlineData("./a.md", false)]
    [InlineData("a//b.md", false)]
    [InlineData("/abs.md", false)]
    [InlineData(".git/config", false)]
    [InlineData("x/.GIT/config", false)]
    [InlineData("a\\b.md", false)]
    [InlineData("c:evil.md", false)]
    [InlineData("tab\there.md", false)]
    public void Path_safety_matches_the_writer_traversal_rule(string path, bool safe) =>
        Assert.Equal(safe, OfflineMergeGit.IsSafePath(path));

    [Fact]
    public async Task A_pushed_symlink_is_rejected()
    {
        string head;
        using (var repo = new Repository(_repo.Checkout))
        {
            var parent = repo.Lookup<Commit>(_base);
            var definition = TreeDefinition.From(parent.Tree);
            definition.Add("link.md", repo.ObjectDatabase.CreateBlob(new MemoryStream("../../etc/passwd"u8.ToArray())), Mode.SymbolicLink);
            head = CommitTree(repo, definition, parent);
        }

        var result = Assert.IsType<VaultOfflineMergeResult.Rejected>(await MergeAsync(head));

        Assert.Contains("symlink 'link.md'", result.Reason, StringComparison.Ordinal);
        Assert.Equal(_base, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task A_frozen_decision_edit_is_rejected_where_the_writer_refuses_it()
    {
        const string decision = "---\nid: x\ntype: decision\ntitle: X\nstatus: accepted\n---\n\nBody.\n";
        var decisionBase = MainCommit("decisions/0001-x.md", decision);
        var head = DeviceCommit(decisionBase, ("decisions/0001-x.md", decision.Replace("Body.", "Changed.", StringComparison.Ordinal)));

        var result = await MergeAsync(head, decisionBase);

        // The writer refuses the same change from an agent branch.
        using (var repo = new Repository(_repo.Checkout))
        {
            repo.CreateBranch("agent/frozen", repo.Lookup<Commit>(head));
        }

        var writer = await _writer.SubmitAsync(
            new VaultEditRequest(Guid.NewGuid(), decisionBase, "agent/frozen", "agent-ana", null, null), CancellationToken.None);

        Assert.Equal(VaultEditOutcome.Refused, writer.Outcome);
        Assert.Equal(
            "changes frozen decision records: " + string.Join(", ", writer.ConflictPaths),
            Assert.IsType<VaultOfflineMergeResult.Rejected>(result).Reason);
        Assert.Equal(decisionBase, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Fact]
    public async Task An_auto_merge_into_a_decision_frozen_after_base_is_rejected()
    {
        var proposed = string.Join('\n', ["---", "id: x", "type: decision", "title: X", "status: proposed", "---", "", "a", "b", "c", "d", "e", "f"]) + "\n";
        var decisionBase = MainCommit("decisions/0001-x.md", proposed);
        var head = DeviceCommit(decisionBase, ("decisions/0001-x.md", proposed.Replace("\nf\n", "\nchanged f\n", StringComparison.Ordinal)));
        var accepted = MainCommit("decisions/0001-x.md", proposed.Replace("status: proposed", "status: accepted", StringComparison.Ordinal));

        var result = await MergeAsync(head, decisionBase);

        Assert.Equal("changes frozen decision records: decisions/0001-x.md", Assert.IsType<VaultOfflineMergeResult.Rejected>(result).Reason);
        Assert.Equal(accepted, TempGitRepository.HeadOf(_repo.Checkout));
        await AssertNoScratchLeftAsync();
    }

    [Fact]
    public async Task Concurrent_pushes_are_serialised_and_both_land()
    {
        const int count = 6;
        var heads = Enumerable.Range(1, count)
            .Select(i => DeviceCommit(_base, ($"note-{i}.md", $"note {i}\n")))
            .ToList();
        MainCommit("doc.md", Edit(_baseDoc, 1, "main 1"));

        var results = await Task.WhenAll(heads.Select((head, i) =>
            _engine.MergeAsync(new VaultOfflineMergeRequest($"device-{i}", "main", head, _base), CancellationToken.None)));

        Assert.All(results, r => Assert.IsType<VaultOfflineMergeResult.Merged>(r));
        for (var i = 1; i <= count; i++)
        {
            Assert.Equal($"note {i}\n", TempGitRepository.ReadFile(_repo.Checkout, $"note-{i}.md"));
        }

        // Each landing moved main from the previous landing, so the lane ran them one at a time.
        Assert.Equal(count, _observer.Commits.Count);
        for (var i = 1; i < count; i++)
        {
            Assert.Equal(_observer.Commits[i - 1].New, _observer.Commits[i].Old);
        }

        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        await AssertNoScratchLeftAsync();
    }

    [Fact]
    public void A_worktree_root_inside_the_main_checkout_is_refused() =>
        Assert.Throws<ArgumentException>(() => new VaultOfflineMergeEngine(_writer, Path.Combine(_repo.Checkout, "scratch")));

    private Task<VaultOfflineMergeResult> MergeAsync(string head, string? baseCommit = null) =>
        _engine.MergeAsync(new VaultOfflineMergeRequest(_device, "main", head, baseCommit ?? _base), CancellationToken.None);

    private async Task AssertNoScratchLeftAsync()
    {
        Assert.Empty(await _git.ListWorktreesAsync(_repo.Checkout, CancellationToken.None));
        using var repo = new Repository(_repo.Checkout);
        Assert.DoesNotContain(repo.Branches, b => b.FriendlyName.StartsWith("offline-merge/", StringComparison.Ordinal));
    }

    private string MainCommit(string path, string content)
    {
        TempGitRepository.WriteFile(_repo.Checkout, path, content);
        return TempGitRepository.CommitAll(_repo.Checkout, $"main edit {path}");
    }

    /// <summary>A device commit on <paramref name="parentSha"/>, written straight into the object database (as a push would).</summary>
    private string DeviceCommit(string parentSha, params (string Path, string? Content)[] changes)
    {
        using var repo = new Repository(_repo.Checkout);
        var parent = repo.Lookup<Commit>(parentSha);
        var definition = TreeDefinition.From(parent.Tree);
        foreach (var (path, content) in changes)
        {
            if (content is null)
            {
                definition.Remove(path);
            }
            else
            {
                definition.Add(path, repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(content))), Mode.NonExecutableFile);
            }
        }

        return CommitTree(repo, definition, parent);
    }

    private string DeviceCommitBytes(string parentSha, string path, byte[] content)
    {
        using var repo = new Repository(_repo.Checkout);
        var parent = repo.Lookup<Commit>(parentSha);
        var definition = TreeDefinition.From(parent.Tree);
        definition.Add(path, repo.ObjectDatabase.CreateBlob(new MemoryStream(content)), Mode.NonExecutableFile);
        return CommitTree(repo, definition, parent);
    }

    private static string CommitTree(Repository repo, TreeDefinition definition, Commit parent)
    {
        var signature = new LibGit2Sharp.Signature("Device", "device@example.invalid", DateTimeOffset.UtcNow);
        var tree = repo.ObjectDatabase.CreateTree(definition);
        return repo.ObjectDatabase.CreateCommit(signature, signature, "offline edit", tree, [parent], prettifyMessage: false).Sha;
    }

    private static string Edit(string text, int line, string replacement)
    {
        var lines = text.Split('\n');
        lines[line - 1] = replacement;
        return string.Join('\n', lines);
    }

    private static void WriteBinary(string workdir, string path, byte[] content) =>
        File.WriteAllBytes(Path.Combine(workdir, path), content);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingObserver : IVaultCommitObserver
    {
        private readonly Lock _gate = new();

        public List<(string Old, string New)> Commits { get; } = [];

        public Task OnCommitAsync(string oldCommit, string newCommit, CancellationToken ct)
        {
            lock (_gate)
            {
                Commits.Add((oldCommit, newCommit));
            }

            return Task.CompletedTask;
        }
    }
}
