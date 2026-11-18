using Bazaar.Contracts.Vms;

namespace Bazaar.Guest.Agent.Tests;

public sealed class WorkspaceFileOpsTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delete_file_removes_a_file_under_the_root()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        Directory.CreateDirectory(Path.Combine(root.Root, "src"));
        var file = Path.Combine(root.Root, "src", "a.txt");
        await File.WriteAllTextAsync(file, "x", _ct);

        await guest.SendAsync(new DeleteFile("src/a.txt"));
        var result = await guest.ReceiveAsync<DeleteFileResult>();

        Assert.True(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal("src/a.txt", result.Path);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Delete_file_reports_a_missing_file_and_refuses_a_directory()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        Directory.CreateDirectory(Path.Combine(root.Root, "dir"));

        await guest.SendAsync(new DeleteFile("nope.txt"));
        var missing = await guest.ReceiveAsync<DeleteFileResult>();
        await guest.SendAsync(new DeleteFile("dir"));
        var directory = await guest.ReceiveAsync<DeleteFileResult>();

        Assert.False(missing.Ok);
        Assert.Contains("missing", missing.Error);
        Assert.False(directory.Ok);
        Assert.Contains("directory", directory.Error);
        Assert.True(Directory.Exists(Path.Combine(root.Root, "dir")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("a\\..\\..\\escape.txt")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(".")]
    public async Task Delete_file_refuses_dot_dot_and_non_files(string path)
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new DeleteFile(path));
        var result = await guest.ReceiveAsync<DeleteFileResult>();

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Delete_file_refuses_an_absolute_path_even_when_it_is_inside_the_root()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var inside = Path.Combine(root.Root, "keep.txt");
        await File.WriteAllTextAsync(inside, "x", _ct);

        await guest.SendAsync(new DeleteFile(inside));
        var result = await guest.ReceiveAsync<DeleteFileResult>();

        Assert.False(result.Ok);
        Assert.True(File.Exists(inside));
    }

    [Fact]
    public async Task Delete_file_through_a_symlink_directory_that_leaves_the_root_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var victim = Path.Combine(outside.Root, "victim.txt");
        await File.WriteAllTextAsync(victim, "x", _ct);
        File.CreateSymbolicLink(Path.Combine(root.Root, "link"), outside.Root);

        await guest.SendAsync(new DeleteFile("link/victim.txt"));
        var result = await guest.ReceiveAsync<DeleteFileResult>();

        Assert.False(result.Ok);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task Delete_file_on_a_symlink_removes_the_link_and_not_its_target()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        var target = Path.Combine(outside.Root, "target.txt");
        await File.WriteAllTextAsync(target, "x", _ct);
        var link = Path.Combine(root.Root, "link.txt");
        File.CreateSymbolicLink(link, target);

        await guest.SendAsync(new DeleteFile("link.txt"));
        var result = await guest.ReceiveAsync<DeleteFileResult>();

        Assert.True(result.Ok);
        Assert.Null(new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(link));
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task List_files_lists_one_level_sorted_with_sizes_and_kinds()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        Directory.CreateDirectory(Path.Combine(root.Root, "sub", "deeper"));
        await File.WriteAllTextAsync(Path.Combine(root.Root, "sub", "b.txt"), "12345", _ct);
        await File.WriteAllTextAsync(Path.Combine(root.Root, "sub", "a.txt"), "", _ct);
        await File.WriteAllTextAsync(Path.Combine(root.Root, "sub", "deeper", "c.txt"), "zz", _ct);

        await guest.SendAsync(new ListFiles("sub"));
        var result = await guest.ReceiveAsync<ListFilesResult>();

        Assert.Null(result.Error);
        Assert.Equal("sub", result.Dir);
        Assert.Equal(
            [new GuestFileEntry("a.txt", 0, false), new GuestFileEntry("b.txt", 5, false), new GuestFileEntry("deeper", 0, true)],
            result.Entries);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    public async Task List_files_accepts_the_root(string dir)
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        await File.WriteAllTextAsync(Path.Combine(root.Root, "top.txt"), "x", _ct);

        await guest.SendAsync(new ListFiles(dir));
        var result = await guest.ReceiveAsync<ListFilesResult>();

        Assert.Null(result.Error);
        Assert.Equal("top.txt", Assert.Single(result.Entries).Name);
    }

    [Fact]
    public async Task List_files_refuses_dot_dot_absolute_and_missing_directories()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        foreach (var dir in new[] { "..", "a/../..", root.Root, "missing" })
        {
            await guest.SendAsync(new ListFiles(dir));
            var result = await guest.ReceiveAsync<ListFilesResult>();

            Assert.NotNull(result.Error);
            Assert.Empty(result.Entries);
        }
    }

    [Fact]
    public async Task List_files_through_a_symlink_that_leaves_the_root_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        await File.WriteAllTextAsync(Path.Combine(outside.Root, "secret.txt"), "x", _ct);
        File.CreateSymbolicLink(Path.Combine(root.Root, "link"), outside.Root);

        await guest.SendAsync(new ListFiles("link"));
        var result = await guest.ReceiveAsync<ListFilesResult>();

        Assert.NotNull(result.Error);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task List_files_over_the_entry_cap_is_an_error_not_a_dead_channel()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        for (var i = 0; i <= 2000; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(root.Root, $"f{i}"), [], _ct);
        }

        await guest.SendAsync(new ListFiles(""));
        var result = await guest.ReceiveAsync<ListFilesResult>();
        await guest.SyncAsync();

        Assert.NotNull(result.Error);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Resolve_real_path_returns_the_relative_path_of_a_plain_file_and_of_the_root()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        Directory.CreateDirectory(Path.Combine(root.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(root.Root, "src", "a.cs"), "x", _ct);

        await guest.SendAsync(new ResolveRealPath("src/./a.cs"));
        var file = await guest.ReceiveAsync<ResolveRealPathResult>();
        await guest.SendAsync(new ResolveRealPath("."));
        var self = await guest.ReceiveAsync<ResolveRealPathResult>();

        Assert.Equal("src/a.cs", file.RealPath);
        Assert.Null(file.Error);
        Assert.Equal(".", self.RealPath);
    }

    [Fact]
    public async Task Resolve_real_path_follows_a_symlink_inside_the_root()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        Directory.CreateDirectory(Path.Combine(root.Root, "real"));
        await File.WriteAllTextAsync(Path.Combine(root.Root, "real", "a.txt"), "x", _ct);
        File.CreateSymbolicLink(Path.Combine(root.Root, "alias"), Path.Combine(root.Root, "real"));

        await guest.SendAsync(new ResolveRealPath("alias/a.txt"));
        var result = await guest.ReceiveAsync<ResolveRealPathResult>();

        Assert.Equal("real/a.txt", result.RealPath);
    }

    [Fact]
    public async Task Resolve_real_path_returns_null_for_a_symlink_that_leaves_the_root()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var outside = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        await File.WriteAllTextAsync(Path.Combine(outside.Root, "secret.txt"), "x", _ct);
        File.CreateSymbolicLink(Path.Combine(root.Root, "dirlink"), outside.Root);
        File.CreateSymbolicLink(Path.Combine(root.Root, "filelink"), Path.Combine(outside.Root, "secret.txt"));

        await guest.SendAsync(new ResolveRealPath("dirlink/secret.txt"));
        var viaDirectory = await guest.ReceiveAsync<ResolveRealPathResult>();
        await guest.SendAsync(new ResolveRealPath("filelink"));
        var viaFile = await guest.ReceiveAsync<ResolveRealPathResult>();

        Assert.Null(viaDirectory.RealPath);
        Assert.Null(viaFile.RealPath);
    }

    [Fact]
    public async Task Resolve_real_path_returns_null_for_a_link_loop()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Creates a symbolic link.");
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        File.CreateSymbolicLink(Path.Combine(root.Root, "a"), Path.Combine(root.Root, "b"));
        File.CreateSymbolicLink(Path.Combine(root.Root, "b"), Path.Combine(root.Root, "a"));

        await guest.SendAsync(new ResolveRealPath("a"));
        var result = await guest.ReceiveAsync<ResolveRealPathResult>();

        Assert.Null(result.RealPath);
    }

    [Fact]
    public async Task Resolve_real_path_refuses_dot_dot_and_absolute_paths_with_a_null_result()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();
        await File.WriteAllTextAsync(Path.Combine(root.Root, "in.txt"), "x", _ct);

        foreach (var path in new[] { "../x", "a/../../x", Path.Combine(root.Root, "in.txt") })
        {
            await guest.SendAsync(new ResolveRealPath(path));
            var result = await guest.ReceiveAsync<ResolveRealPathResult>();

            Assert.Null(result.RealPath);
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task Resolve_real_path_of_a_missing_file_inside_the_root_still_resolves()
    {
        await using var root = new TempRoot();
        await using var guest = new GuestHarness(root.Options());
        await guest.ReceiveAsync<Hello>();

        await guest.SendAsync(new ResolveRealPath("new/dir/file.txt"));
        var result = await guest.ReceiveAsync<ResolveRealPathResult>();

        Assert.Equal("new/dir/file.txt", result.RealPath);
    }
}
