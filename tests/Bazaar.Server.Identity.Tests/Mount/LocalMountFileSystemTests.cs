using System.Text;
using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Mount;

namespace Bazaar.Server.Tests.Identity.Mount;

public sealed class LocalMountFileSystemTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "bazaar-mount-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_base))
        {
            Directory.Delete(_base, recursive: true);
        }
    }

    [Fact]
    public void Writes_land_under_the_root_and_list_as_relative_paths()
    {
        var root = Path.Combine(_base, "mount");
        var fs = new LocalMountFileSystem(root, []);

        fs.WriteFile("codex/services.json", "{}"u8.ToArray());

        Assert.True(File.Exists(Path.Combine(root, "codex", "services.json")));
        Assert.True(fs.Exists("codex/services.json"));
        Assert.Equal(["codex/services.json"], fs.ListFiles("codex"));
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("codex/../../escape.json")]
    [InlineData("/etc/passwd")]
    [InlineData("codex\\escape.json")]
    public void Paths_that_escape_the_root_are_refused(string relativePath)
    {
        var root = Path.Combine(_base, "mount");
        var fs = new LocalMountFileSystem(root, []);

        Assert.ThrowsAny<ArgumentException>(() => fs.WriteFile(relativePath, "{}"u8.ToArray()));
        Assert.False(File.Exists(Path.Combine(_base, "escape.json")));
    }

    [Fact]
    public void A_root_inside_a_forbidden_worktree_is_refused()
    {
        var worktree = Path.Combine(_base, "worktree");
        var root = Path.Combine(worktree, "mounts", "codex");

        Assert.Throws<InvalidOperationException>(() => new LocalMountFileSystem(root, [worktree]));
    }

    [Fact]
    public void A_forbidden_root_inside_the_mount_is_refused()
    {
        var root = Path.Combine(_base, "mount");
        var worktreeInside = Path.Combine(root, "codex", "worktree");

        Assert.Throws<InvalidOperationException>(() => new LocalMountFileSystem(root, [worktreeInside]));
    }

    [Fact]
    public void A_mount_equal_to_a_forbidden_root_is_refused()
    {
        var root = Path.Combine(_base, "mount");

        Assert.Throws<InvalidOperationException>(() => new LocalMountFileSystem(root, [root]));
    }

    [Fact]
    public void A_sibling_with_a_shared_name_prefix_does_not_count_as_overlap()
    {
        var root = Path.Combine(_base, "mount");
        var sibling = Path.Combine(_base, "mount-worktree");

        var fs = new LocalMountFileSystem(root, [sibling]);

        Assert.Equal(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), fs.Root);
    }

    [Fact]
    public void A_relative_root_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new LocalMountFileSystem("relative/mount", []));
    }

    [Fact]
    public void Detach_removes_the_agent_directory_and_refuses_the_root()
    {
        var root = Path.Combine(_base, "mount");
        var fs = new LocalMountFileSystem(root, []);
        fs.WriteFile("codex/services.json", "{}"u8.ToArray());
        fs.WriteFile("other/services.json", "{}"u8.ToArray());

        fs.Detach("codex");

        Assert.False(Directory.Exists(Path.Combine(root, "codex")));
        Assert.True(File.Exists(Path.Combine(root, "other", "services.json")));
        Assert.Throws<ArgumentException>(() => fs.Detach(string.Empty));
    }

    [Fact]
    public void Writes_through_a_link_inside_the_root_are_refused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(_base, "mount");
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.CreateSymbolicLink(Path.Combine(root, "codex"), outside);
        var fs = new LocalMountFileSystem(root, []);

        Assert.Throws<InvalidOperationException>(() => fs.WriteFile("codex/services.json", "{}"u8.ToArray()));
        Assert.False(File.Exists(Path.Combine(outside, "services.json")));
    }

    [Fact]
    public void The_writer_on_disk_detaches_and_leaves_no_agent_files()
    {
        var root = Path.Combine(_base, "mount");
        var writer = new MountedFolderWriter(new LocalMountFileSystem(root, []));
        writer.Materialize("codex", [new MountEntry("services.json", MountedFileKind.Metadata, null, Encoding.UTF8.GetBytes("{}"))]);
        writer.SeedCanaries("codex", ["github"]);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(root, "codex")).Length);

        writer.Detach("codex", "hard taint");

        Assert.False(Directory.Exists(Path.Combine(root, "codex")));
        Assert.Throws<InvalidOperationException>(() => writer.Materialize("codex", [new MountEntry("services.json", MountedFileKind.Metadata, null, "{}"u8.ToArray())]));
    }
}
