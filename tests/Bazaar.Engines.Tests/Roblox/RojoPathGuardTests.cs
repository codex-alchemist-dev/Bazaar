using Bazaar.Engines.Roblox.Rojo;

namespace Bazaar.Engines.Tests.Roblox;

public sealed class RojoPathGuardTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("bazaar-rojo-guard-").FullName;
    private readonly string _root;

    public RojoPathGuardTests()
    {
        _root = Path.Combine(_base, "proj");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "default.project.json"), "{}");
    }

    public void Dispose()
    {
        Directory.Delete(_base, recursive: true);
    }

    [Fact]
    public void Dot_dot_that_returns_inside_root_is_allowed()
    {
        string full = RojoPathGuard.ResolveProjectFile(_root, Path.Combine("src", "..", "src", "default.project.json"));

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "src", "default.project.json")), full);
    }

    [Fact]
    public void Relative_project_resolves_under_root()
    {
        string full = RojoPathGuard.ResolveProjectFile(_root, Path.Combine("src", "default.project.json"));

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "src", "default.project.json")), full);
    }

    [Fact]
    public void Dot_dot_escape_is_rejected()
    {
        File.WriteAllText(Path.Combine(_base, "outside.project.json"), "{}");

        RojoException ex = Assert.Throws<RojoException>(() =>
            RojoPathGuard.ResolveProjectFile(_root, Path.Combine("..", "outside.project.json")));

        Assert.Equal(RojoErrorKind.PathOutsideRoot, ex.Kind);
    }

    [Fact]
    public void Absolute_path_outside_root_is_rejected()
    {
        string outside = Path.Combine(_base, "other.project.json");
        File.WriteAllText(outside, "{}");

        RojoException ex = Assert.Throws<RojoException>(() => RojoPathGuard.ResolveProjectFile(_root, outside));

        Assert.Equal(RojoErrorKind.PathOutsideRoot, ex.Kind);
    }

    [Fact]
    public void Sibling_with_shared_name_prefix_is_rejected()
    {
        // "proj2" starts with "proj" but is not inside it.
        string sibling = Path.Combine(_base, "proj2");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "default.project.json"), "{}");

        RojoException ex = Assert.Throws<RojoException>(() =>
            RojoPathGuard.ResolveProjectFile(_root, Path.Combine("..", "proj2", "default.project.json")));

        Assert.Equal(RojoErrorKind.PathOutsideRoot, ex.Kind);
    }

    [Fact]
    public void Symlink_below_root_that_points_outside_is_rejected()
    {
        string outsideDir = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outsideDir);
        File.WriteAllText(Path.Combine(outsideDir, "default.project.json"), "{}");
        string link = Path.Combine(_root, "linked");
        Assert.SkipUnless(TryCreateDirectoryLink(link, outsideDir), "Symbolic links are not available here.");

        RojoException ex = Assert.Throws<RojoException>(() =>
            RojoPathGuard.ResolveProjectFile(_root, Path.Combine("linked", "default.project.json")));

        Assert.Equal(RojoErrorKind.PathOutsideRoot, ex.Kind);
    }

    [Fact]
    public void Empty_path_is_rejected()
    {
        RojoException ex = Assert.Throws<RojoException>(() => RojoPathGuard.ResolveProjectFile(_root, " "));

        Assert.Equal(RojoErrorKind.PathOutsideRoot, ex.Kind);
    }

    [Fact]
    public void Missing_root_is_ProjectNotFound()
    {
        RojoException ex = Assert.Throws<RojoException>(() =>
            RojoPathGuard.ResolveProjectFile(Path.Combine(_base, "absent"), "default.project.json"));

        Assert.Equal(RojoErrorKind.ProjectNotFound, ex.Kind);
    }

    [Fact]
    public void Output_file_need_not_exist_but_must_be_under_root()
    {
        string full = RojoPathGuard.ResolveOutputFile(_root, Path.Combine("src", "game.rbxlx"));

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "src", "game.rbxlx")), full);
        Assert.Equal(RojoErrorKind.PathOutsideRoot, Assert.Throws<RojoException>(() =>
            RojoPathGuard.ResolveOutputFile(_root, Path.Combine("..", "game.rbxlx"))).Kind);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
