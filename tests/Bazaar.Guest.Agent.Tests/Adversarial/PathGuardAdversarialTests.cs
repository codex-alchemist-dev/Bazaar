namespace Bazaar.Guest.Agent.Tests.Adversarial;

/// <summary>The host is trusted, but a path it relays must still stay inside the allowed roots, whatever it is spelled like or links to.</summary>
public sealed class PathGuardAdversarialTests : IAsyncDisposable
{
    private readonly TempRoot _sandbox = new();
    private readonly string _root;
    private readonly string _outside;

    public PathGuardAdversarialTests()
    {
        _root = Path.Combine(_sandbox.Root, "root");
        _outside = Path.Combine(_sandbox.Root, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret"), "x");
    }

    public ValueTask DisposeAsync() => _sandbox.DisposeAsync();

    [Fact]
    public void A_plain_file_inside_the_root_resolves()
    {
        var guard = new PathGuard([_root]);

        Assert.Equal(Path.Combine(RealRoot(), "a.txt"), guard.Resolve(Path.Combine(_root, "a.txt")));
    }

    [Theory]
    [InlineData("../outside/secret")]
    [InlineData("sub/../../outside/secret")]
    [InlineData("../root/../outside/secret")]
    [InlineData("../../../../../../../../etc/passwd")]
    public void Dot_dot_traversal_out_of_the_root_is_refused(string relative)
    {
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(Path.Combine(_root, relative)));
        Assert.Null(guard.ResolveDirectory(Path.Combine(_root, relative)));
    }

    [Fact]
    public void Dot_dot_that_stays_inside_the_root_is_allowed_and_normalised()
    {
        var guard = new PathGuard([_root]);

        Assert.Equal(Path.Combine(RealRoot(), "b"), guard.Resolve(Path.Combine(_root, "a", "..", "b")));
    }

    [Fact]
    public void The_root_itself_is_not_a_file_path_but_is_a_directory_path()
    {
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(_root));
        Assert.Null(guard.Resolve(_root + Path.DirectorySeparatorChar));
        Assert.Null(guard.Resolve(Path.Combine(_root, ".")));
        Assert.Equal(RealRoot(), guard.ResolveDirectory(_root));
    }

    [Fact]
    public void A_sibling_whose_name_starts_with_the_root_name_is_outside()
    {
        var evil = _root + "-evil";
        Directory.CreateDirectory(evil);
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(Path.Combine(evil, "f")));
        Assert.Null(guard.ResolveDirectory(evil));
        Assert.Null(guard.Resolve(_root + "x"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.txt")]
    [InlineData("./a.txt")]
    [InlineData("../root/a.txt")]
    [InlineData("root/a.txt")]
    public void Empty_and_relative_paths_are_refused(string path)
    {
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(path));
        Assert.Null(guard.ResolveDirectory(path));
    }

    [Fact]
    public void Backslashes_on_a_posix_guest_are_part_of_one_file_name_and_never_a_separator()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Backslash is a separator on Windows.");
        var guard = new PathGuard([_root]);

        var resolved = guard.Resolve(_root + "/..\\..\\outside\\secret");

        // Either refused, or one literal name directly under the root: it must never reach the outside directory.
        Assert.True(resolved is null || Path.GetDirectoryName(resolved) == RealRoot(), $"Resolved to '{resolved}'.");
        Assert.Null(guard.Resolve("\\etc\\passwd"));
    }

    [Fact]
    public void A_symlink_to_a_directory_outside_the_root_is_refused_for_the_link_and_for_what_is_behind_it()
    {
        var link = Link(Path.Combine(_root, "out"), _outside);
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(link));
        Assert.Null(guard.Resolve(Path.Combine(link, "secret")));
        Assert.Null(guard.ResolveDirectory(link));
    }

    [Fact]
    public void A_symlink_to_a_file_outside_the_root_is_refused()
    {
        var link = Link(Path.Combine(_root, "secret-link"), Path.Combine(_outside, "secret"));
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(link));
    }

    [Fact]
    public void A_dangling_symlink_to_outside_is_refused_so_a_write_cannot_create_the_target()
    {
        var link = Link(Path.Combine(_root, "dangling"), Path.Combine(_outside, "not-yet"));
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(link));
    }

    [Fact]
    public void A_chain_of_symlinks_that_ends_outside_is_refused_at_every_link()
    {
        var third = Link(Path.Combine(_root, "l3"), _outside);
        var second = Link(Path.Combine(_root, "l2"), third);
        var first = Link(Path.Combine(_root, "l1"), second);
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(first));
        Assert.Null(guard.Resolve(Path.Combine(first, "secret")));
        Assert.Null(guard.Resolve(second));
    }

    [Fact]
    public void A_relative_symlink_that_climbs_out_of_the_root_is_refused()
    {
        var link = Path.Combine(_root, "up");
        Link(link, Path.Combine("..", "outside"));
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(Path.Combine(link, "secret")));
    }

    [Fact]
    public void A_symlink_loop_is_refused_and_does_not_hang()
    {
        var a = Path.Combine(_root, "a");
        var b = Path.Combine(_root, "b");
        Link(a, b);
        Link(b, a);
        Link(Path.Combine(_root, "self"), Path.Combine(_root, "self"));
        var guard = new PathGuard([_root]);

        Assert.Null(guard.Resolve(a));
        Assert.Null(guard.Resolve(Path.Combine(a, "f")));
        Assert.Null(guard.Resolve(Path.Combine(_root, "self")));
    }

    [Fact]
    public void A_symlink_that_stays_inside_the_root_resolves_to_its_real_target()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        var link = Link(Path.Combine(_root, "alias"), Path.Combine(_root, "real"));
        var guard = new PathGuard([_root]);

        Assert.Equal(Path.Combine(RealRoot(), "real", "f"), guard.Resolve(Path.Combine(link, "f")));
    }

    [Fact]
    public void A_root_that_is_itself_a_symlink_is_judged_by_where_it_really_points()
    {
        var aliasRoot = Link(Path.Combine(_sandbox.Root, "alias-root"), _root);
        var guard = new PathGuard([aliasRoot]);

        Assert.Equal(Path.Combine(RealRoot(), "f"), guard.Resolve(Path.Combine(aliasRoot, "f")));
        Assert.Null(guard.Resolve(Path.Combine(_outside, "secret")));
    }

    [Fact]
    public void With_no_roots_nothing_resolves()
    {
        var guard = new PathGuard([]);

        Assert.Null(guard.Resolve(Path.Combine(_root, "a")));
        Assert.Null(guard.ResolveDirectory(_root));
    }

    private string RealRoot() => new PathGuard([_root]).ResolveDirectory(_root)!;

    private static string Link(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic links cannot be created here: {ex.Message}");
        }

        return path;
    }
}
