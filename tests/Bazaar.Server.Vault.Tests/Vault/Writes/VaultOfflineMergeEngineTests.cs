using Bazaar.Server.Tests.Vault.Git;
using Bazaar.Server.Vault.Git;
using Bazaar.Server.Vault.Writes;
using LibGit2Sharp;

namespace Bazaar.Server.Tests.Vault.Writes;

/// <summary>Offline Vault commit replay: clean merge, kept conflict branch, rejection and no lost edit (doc 56 section 5 item 8).</summary>
public sealed class VaultOfflineMergeEngineTests : IDisposable
{
    private static readonly string _baseDoc = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line {i}")) + "\n";
    private static readonly DateTimeOffset _at = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);

    private readonly TempGitRepository _repo;
    private readonly LibGit2GitProvider _git = new();
    private readonly VaultWriter _writer;
    private readonly VaultOfflineMergeEngine _engine;

    public VaultOfflineMergeEngineTests()
    {
        _repo = new TempGitRepository(("doc.md", _baseDoc), ("other.md", _baseDoc));
        _writer = new VaultWriter(_git, _repo.Checkout, TempGitRepository.Writer);
        _engine = new VaultOfflineMergeEngine(_writer, _git, _git, _repo.Checkout);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _repo.Dispose();
    }

    [Fact]
    public async Task A_clean_offline_commit_lands_on_main_with_the_offline_trailers()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        var device = Guid.NewGuid();
        var write = Guid.NewGuid();
        var head = await DeviceBranchAsync("dev-a", "doc.md", 10, "device 10");
        await EditMain("other.md", 1, "main 1");

        var result = await _engine.MergeAsync(Request(device, write, "dev-a", head, baseCommit), CancellationToken.None);

        var merged = Assert.IsType<VaultOfflineMergeResult.Merged>(result);
        Assert.Equal(TempGitRepository.HeadOf(_repo.Checkout), merged.NewHead);
        using var repo = new Repository(_repo.Checkout);
        var message = repo.Lookup<Commit>(merged.NewHead).Message;
        Assert.Contains($"Bazaar-Device: {device:D}", message);
        Assert.Contains("Bazaar-Offline-Branch: dev-a", message);
        Assert.Contains($"Bazaar-Offline-Write: {write:D}", message);
        Assert.Contains($"Bazaar-Edit-Base: {baseCommit}", message);
        Assert.Contains("device 10", TempGitRepository.ReadFile(_repo.Checkout, "doc.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_overlapping_commit_is_kept_on_a_conflict_branch_and_main_is_untouched()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        var device = Guid.NewGuid();
        var head = await DeviceBranchAsync("dev-b", "doc.md", 5, "device 5");
        await EditMain("doc.md", 5, "main 5");
        var mainHead = TempGitRepository.HeadOf(_repo.Checkout);

        var result = await _engine.MergeAsync(Request(device, Guid.NewGuid(), "dev-b", head, baseCommit), CancellationToken.None);

        var conflicted = Assert.IsType<VaultOfflineMergeResult.Conflicted>(result);
        Assert.StartsWith($"conflict/{device:D}/20261122T180000Z", conflicted.BranchName, StringComparison.Ordinal);
        Assert.Equal(["doc.md"], conflicted.ConflictedPaths);
        Assert.Equal(mainHead, TempGitRepository.HeadOf(_repo.Checkout));
        Assert.Equal(head, TempGitRepository.TipOf(_repo.Checkout, conflicted.BranchName));
        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        Assert.Contains("main 5", TempGitRepository.ReadFile(_repo.Checkout, "doc.md"), StringComparison.Ordinal);
        var kept = await _git.ReadTextAsync(_repo.Checkout, conflicted.BranchName, "doc.md", CancellationToken.None);
        Assert.Contains("device 5", kept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replaying_a_landed_commit_reports_merged_without_a_new_commit()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        var head = await DeviceBranchAsync("dev-c", "doc.md", 3, "device 3");
        var request = Request(Guid.NewGuid(), Guid.NewGuid(), "dev-c", head, baseCommit);

        var first = Assert.IsType<VaultOfflineMergeResult.Merged>(await _engine.MergeAsync(request, CancellationToken.None));
        var second = Assert.IsType<VaultOfflineMergeResult.Merged>(await _engine.MergeAsync(request, CancellationToken.None));

        Assert.Equal(first.NewHead, second.NewHead);
    }

    [Fact]
    public async Task A_branch_that_does_not_point_at_the_pushed_commit_is_rejected()
    {
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        await DeviceBranchAsync("dev-d", "doc.md", 3, "device 3");
        var mainHead = TempGitRepository.HeadOf(_repo.Checkout);

        var wrongHead = await _engine.MergeAsync(
            Request(Guid.NewGuid(), Guid.NewGuid(), "dev-d", new string('a', 40), baseCommit), CancellationToken.None);
        var missing = await _engine.MergeAsync(
            Request(Guid.NewGuid(), Guid.NewGuid(), "nope", mainHead, baseCommit), CancellationToken.None);

        Assert.IsType<VaultOfflineMergeResult.Rejected>(wrongHead);
        Assert.IsType<VaultOfflineMergeResult.Rejected>(missing);
        Assert.Equal(mainHead, TempGitRepository.HeadOf(_repo.Checkout));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task No_offline_edit_is_lost_in_any_replay_order(int seed)
    {
        var random = new Random(seed);
        var baseCommit = TempGitRepository.HeadOf(_repo.Checkout);
        const int devices = 6;
        var intents = new List<(Guid Device, Guid Write, string Branch, string Head, string Marker)>();
        for (var i = 0; i < devices; i++)
        {
            // A few devices share lines, so some replays conflict and some merge.
            var line = random.Next(1, 5);
            var marker = $"device {i} on line {line}";
            var branch = $"dev-{i}";
            var head = await DeviceBranchAsync(branch, "doc.md", line, marker);
            intents.Add((Guid.NewGuid(), Guid.NewGuid(), branch, head, marker));
        }

        foreach (var intent in intents.OrderBy(_ => random.Next()))
        {
            var request = Request(intent.Device, intent.Write, intent.Branch, intent.Head, baseCommit);
            var result = await _engine.MergeAsync(request, CancellationToken.None);
            var text = result switch
            {
                VaultOfflineMergeResult.Merged => TempGitRepository.ReadFile(_repo.Checkout, "doc.md"),
                VaultOfflineMergeResult.Conflicted c => await _git.ReadTextAsync(_repo.Checkout, c.BranchName, "doc.md", CancellationToken.None),
                _ => throw new InvalidOperationException("Unexpected result " + result),
            };
            Assert.Contains(intent.Marker, text.Replace("\r\n", "\n"), StringComparison.Ordinal);
        }

        Assert.Equal(0, TempGitRepository.DirtyEntryCount(_repo.Checkout));
        GitIntegrity.AssertReachableObjectsIntact(_repo.Checkout);
    }

    private static VaultOfflineMergeRequest Request(Guid device, Guid write, string branch, string head, string baseCommit) =>
        new(write, device, branch, head, baseCommit, _at);

    private async Task<string> DeviceBranchAsync(string branch, string file, int line, string text)
    {
        var worktree = _repo.WorktreePath("wt-" + branch);
        await _git.AddWorktreeAsync(_repo.Checkout, branch, worktree, CancellationToken.None);
        return await EditAndCommit(worktree, file, line, text);
    }

    private async Task EditMain(string file, int line, string text) => await EditAndCommit(_repo.Checkout, file, line, text);

    private static async Task<string> EditAndCommit(string workdir, string file, int line, string text)
    {
        var lines = File.ReadAllLines(Path.Combine(workdir, file));
        lines[line - 1] = text;
        await File.WriteAllTextAsync(Path.Combine(workdir, file), string.Join('\n', lines) + "\n");
        return TempGitRepository.CommitAll(workdir, $"edit {file} line {line}");
    }
}
