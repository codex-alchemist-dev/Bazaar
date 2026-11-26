using System.Text;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingGit.Fakes;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.Host.Coding;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class WorktreeFilesSourceTests : IDisposable
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly RepoRef _repo = new("repo", "main");

    private readonly string _base = Path.Combine(Path.GetTempPath(), "bazaar-wtfiles-" + Guid.NewGuid().ToString("N"));
    private readonly string _share;
    private readonly string _passport;
    private readonly string _outside;
    private readonly CodingGitService _service;

    public WorktreeFilesSourceTests()
    {
        _share = Path.Combine(_base, "share");
        _passport = Path.Combine(_base, "passport");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_share);
        Directory.CreateDirectory(_passport);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "top secret");
        _service = new CodingGitService(
            new FixedWorkShareLayout(_share, _passport),
            new InMemoryCodingGitEngine(),
            new FakeHostPushPort(),
            new FakePushGate(),
            new FakeAgentGitIdentity(),
            new WorktreeLocks());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the temp folder is private to this test.
        }
    }

    private async Task<(CodingWorktree Worktree, string Path)> OpenAsync(string slug = "task-a")
    {
        var worktree = await _service.CreateWorktreeAsync(new CreateWorktreeRequest("charles", _repo, slug, "gc-1", "task-1"), _ct);
        Directory.CreateDirectory(worktree.Path);
        return (worktree, worktree.Path);
    }

    private WorktreeFilesSource Source() => new(new FixedWorkShareLayout(_share, _passport));

    private async Task<IWorktreeFiles> StoreAsync(string slug = "task-a")
    {
        var (worktree, _) = await OpenAsync(slug);
        return Source().FilesFor(worktree) ?? throw new InvalidOperationException("no store");
    }

    private static bool CanSymlink(string tmp)
    {
        try
        {
            var link = Path.Combine(tmp, "probe-link");
            Directory.CreateSymbolicLink(link, tmp);
            Directory.Delete(link);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public async Task A_file_in_the_worktree_is_read_with_its_hash_and_missing_files_are_null()
    {
        var (worktree, path) = await OpenAsync();
        Directory.CreateDirectory(Path.Combine(path, "src"));
        await File.WriteAllTextAsync(Path.Combine(path, "src", "a.cs"), "hello", _ct);
        var files = Source().FilesFor(worktree)!;

        var file = await files.ReadAsync("src/a.cs", _ct);

        Assert.NotNull(file);
        Assert.Equal("hello", Encoding.UTF8.GetString(file.Content));
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file.Content)), file.Sha256);
        Assert.Null(await files.ReadAsync("src/none.cs", _ct));
        Assert.Null(await files.ReadAsync("src", _ct));
        Assert.True(await files.ExistsAsync("src/a.cs", _ct));
        Assert.False(await files.ExistsAsync("src/none.cs", _ct));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\system32")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("C:win.ini")]
    [InlineData("../outside/secret.txt")]
    [InlineData("a/../../outside/secret.txt")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a\\b")]
    [InlineData("file.txt:stream")]
    [InlineData("a/")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("dir/COM1")]
    [InlineData("dir/lpt9.log")]
    [InlineData("aux.tar.gz")]
    [InlineData(".git")]
    [InlineData(".GIT/config")]
    [InlineData("sub/.git/HEAD")]
    [InlineData("bad\0name")]
    [InlineData("bad\nname")]
    [InlineData("wild*card")]
    public async Task Unsafe_paths_are_refused_by_every_operation(string path)
    {
        var files = await StoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(path, _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ListAsync(path, _ct));
        Assert.Null(await files.ResolveRealPathAsync(path, _ct));
        Assert.False(await files.ExistsAsync(path, _ct));
    }

    [Fact]
    public async Task A_path_longer_than_the_cap_and_an_empty_file_path_are_refused()
    {
        var files = await StoreAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(new string('a', 5000), _ct));
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync("", _ct));
    }

    [Fact]
    public async Task A_symlink_that_leaves_the_worktree_is_unresolvable_and_unreadable()
    {
        var (worktree, path) = await OpenAsync();
        if (!CanSymlink(_base))
        {
            return;
        }

        File.CreateSymbolicLink(Path.Combine(path, "leak.txt"), Path.Combine(_outside, "secret.txt"));
        Directory.CreateSymbolicLink(Path.Combine(path, "out"), _outside);
        var files = Source().FilesFor(worktree)!;

        Assert.Null(await files.ResolveRealPathAsync("leak.txt", _ct));
        Assert.Null(await files.ResolveRealPathAsync("out/secret.txt", _ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.ReadAsync("leak.txt", _ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.ReadAsync("out/secret.txt", _ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.ListAsync("out", _ct));
        Assert.False(await files.ExistsAsync("out/secret.txt", _ct));
    }

    [Fact]
    public async Task A_relative_symlink_that_climbs_out_is_unresolvable()
    {
        var (worktree, path) = await OpenAsync();
        if (!CanSymlink(_base))
        {
            return;
        }

        File.CreateSymbolicLink(Path.Combine(path, "up.txt"), Path.Combine("..", "..", "outside", "secret.txt"));
        var files = Source().FilesFor(worktree)!;

        Assert.Null(await files.ResolveRealPathAsync("up.txt", _ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => files.ReadAsync("up.txt", _ct));
    }

    [Fact]
    public async Task A_symlink_that_stays_inside_resolves_to_its_real_relative_path_and_loops_fail_closed()
    {
        var (worktree, path) = await OpenAsync();
        if (!CanSymlink(_base))
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(path, "real"));
        await File.WriteAllTextAsync(Path.Combine(path, "real", "f.txt"), "x", _ct);
        Directory.CreateSymbolicLink(Path.Combine(path, "alias"), Path.Combine(path, "real"));
        File.CreateSymbolicLink(Path.Combine(path, "loop-a"), Path.Combine(path, "loop-b"));
        File.CreateSymbolicLink(Path.Combine(path, "loop-b"), Path.Combine(path, "loop-a"));
        var files = Source().FilesFor(worktree)!;

        Assert.Equal("real/f.txt", await files.ResolveRealPathAsync("alias/f.txt", _ct));
        Assert.Equal("x", Encoding.UTF8.GetString((await files.ReadAsync("alias/f.txt", _ct))!.Content));
        Assert.Null(await files.ResolveRealPathAsync("loop-a", _ct));
    }

    [Fact]
    public async Task A_missing_last_component_resolves_but_a_missing_middle_one_does_not()
    {
        var files = await StoreAsync();

        Assert.Equal("new.txt", await files.ResolveRealPathAsync("new.txt", _ct));
        Assert.Null(await files.ResolveRealPathAsync("nodir/new.txt", _ct));
    }

    [Fact]
    public async Task A_file_over_the_read_cap_is_cut_one_byte_past_the_cap_so_the_handler_refuses_it()
    {
        var (worktree, path) = await OpenAsync();
        await File.WriteAllBytesAsync(Path.Combine(path, "big.bin"), new byte[ReadToolHandler.MaxFileBytes + 5000], _ct);
        var files = Source().FilesFor(worktree)!;

        var file = await files.ReadAsync("big.bin", _ct);

        Assert.Equal(ReadToolHandler.MaxFileBytes + 1, file!.Content.Length);
    }

    [Fact]
    public async Task Listing_is_sorted_recursive_and_skips_git_and_directory_links()
    {
        var (worktree, path) = await OpenAsync();
        Directory.CreateDirectory(Path.Combine(path, "b", "deep"));
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        await File.WriteAllTextAsync(Path.Combine(path, "b", "deep", "z.txt"), "z", _ct);
        await File.WriteAllTextAsync(Path.Combine(path, "a.txt"), "a", _ct);
        await File.WriteAllTextAsync(Path.Combine(path, ".git", "config"), "c", _ct);
        await File.WriteAllTextAsync(Path.Combine(path, "nul.txt"), "device name", _ct);
        if (CanSymlink(_base))
        {
            Directory.CreateSymbolicLink(Path.Combine(path, "out"), _outside);
        }

        var files = Source().FilesFor(worktree)!;

        Assert.Equal(["a.txt", "b/deep/z.txt"], await files.ListAsync("", _ct));
        Assert.Equal(["b/deep/z.txt"], await files.ListAsync("b", _ct));
    }

    [Fact]
    public async Task Write_and_delete_are_not_offered()
    {
        var files = await StoreAsync();

        await Assert.ThrowsAsync<NotSupportedException>(() => files.WriteAsync("a.txt", [1], "00", _ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => files.DeleteAsync("a.txt", _ct));
    }

    [Fact]
    public async Task A_disposed_worktree_gets_no_store()
    {
        var (worktree, _) = await OpenAsync();
        await worktree.DisposeAsync();

        Assert.Null(Source().FilesFor(worktree));
    }

    [Fact]
    public async Task A_worktree_directory_that_is_gone_gets_no_store()
    {
        var (worktree, path) = await OpenAsync();
        Directory.Delete(path, recursive: true);

        Assert.Null(Source().FilesFor(worktree));
    }

    [Fact]
    public async Task A_worktree_whose_layout_root_has_moved_gets_no_store()
    {
        var (worktree, _) = await OpenAsync();
        var elsewhere = new WorktreeFilesSource(new FixedWorkShareLayout(Path.Combine(_base, "other-share"), _passport));

        Assert.Null(elsewhere.FilesFor(worktree));
    }

    [Fact]
    public async Task A_layout_that_throws_gets_no_store()
    {
        var (worktree, _) = await OpenAsync();

        Assert.Null(new WorktreeFilesSource(new ThrowingLayout()).FilesFor(worktree));
    }

    [Fact]
    public async Task A_worktree_whose_path_links_out_of_the_share_gets_no_store()
    {
        if (!CanSymlink(_base))
        {
            return;
        }

        var (worktree, path) = await OpenAsync();
        // Swap the worktree directory for a link to a folder outside the share, after the worktree was opened.
        Directory.Delete(path, recursive: true);
        Directory.CreateSymbolicLink(path, _outside);

        Assert.Null(Source().FilesFor(worktree));
    }

    private sealed class ThrowingLayout : IWorkShareLayout
    {
        public string GetWorktreeRoot(string agentId) => throw new InvalidOperationException("unknown agent");

        public string GetPassportFolder(string agentId) => throw new InvalidOperationException("unknown agent");
    }
}
