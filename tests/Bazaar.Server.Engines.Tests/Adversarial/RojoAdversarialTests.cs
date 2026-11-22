using Bazaar.Server.Engines.Roblox.Rojo;
using Bazaar.Server.Engines.Tests.Roblox;

namespace Bazaar.Server.Engines.Tests.Adversarial;

/// <summary>
/// Attacks on the Rojo path guard, the project-file validator and the runner: an agent passing crafted paths, and a hostile project
/// directory whose files the agent wrote. Each test asserts the attack is refused.
/// </summary>
public sealed class RojoAdversarialTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("bazaar-rojo-adv-").FullName;
    private readonly string _root;
    private readonly string _outside;

    public RojoAdversarialTests()
    {
        _root = Path.Combine(_base, "proj");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "default.project.json"), "{ \"name\": \"p\", \"tree\": { \"$className\": \"DataModel\" } }");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private static RojoRunner RunnerWith(FakeProcessRunner fake) => new(new RojoOptions { BinaryPath = "rojo" }, fake);

    [Theory]
    [InlineData("../outside/secret.txt")]
    [InlineData("src/../../outside/secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("//server/share/x.project.json")]
    [InlineData("C:x.project.json")]
    [InlineData("C:\\x.project.json")]
    [InlineData("default.project.json:stream")]
    [InlineData("default.project.json.")]
    [InlineData("default.project.json ")]
    [InlineData("src/CON")]
    [InlineData("src/nul.json")]
    [InlineData("src/COM1.txt")]
    [InlineData("line\nbreak.project.json")]
    [InlineData("nul\0byte.project.json")]
    public void Crafted_output_paths_never_resolve(string candidate)
    {
        Assert.Throws<RojoException>(() => RojoPathGuard.ResolveOutputFile(_root, candidate));
    }

    [Fact]
    public void A_unc_path_is_outside_the_root_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths exist on Windows only.");

        Assert.Throws<RojoException>(() => RojoPathGuard.ResolveOutputFile(_root, "\\\\server\\share\\x.project.json"));
    }

    [Fact]
    public async Task A_path_starting_with_a_dash_cannot_inject_an_option()
    {
        Directory.CreateDirectory(Path.Combine(_root, "-evil"));
        File.WriteAllText(Path.Combine(_root, "-evil", "--help.project.json"), "{}");
        var fake = new FakeProcessRunner();

        await RunnerWith(fake).BuildAsync(_root, "-evil/--help.project.json", "--output", TestContext.Current.CancellationToken);

        // Both paths reach rojo absolute, so neither can be read as an option.
        var arguments = Assert.Single(fake.Calls).Arguments;
        Assert.All([arguments[1], arguments[3]], argument => Assert.True(Path.IsPathRooted(argument), argument));
    }

    [Fact]
    public void A_sibling_directory_sharing_the_root_as_a_string_prefix_is_outside()
    {
        string sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "default.project.json"), "{}");

        // The sibling is reached by absolute path, and by a relative path that climbs out.
        Assert.Throws<RojoException>(() => RojoPathGuard.ResolveProjectFile(_root, Path.Combine(sibling, "default.project.json")));
        Assert.Throws<RojoException>(() => RojoPathGuard.ResolveProjectFile(_root, "../proj-evil/default.project.json"));
        Directory.Delete(sibling, recursive: true);
    }

    [Fact]
    public void Case_variants_do_not_slip_a_case_sensitive_prefix_check_on_a_case_insensitive_name()
    {
        if (OperatingSystem.IsWindows())
        {
            // Case-insensitive host: the spelling of the root does not matter, and the result is still inside.
            string upper = RojoPathGuard.ResolveProjectFile(_root.ToUpperInvariant(), "default.project.json");
            Assert.StartsWith(_root, upper, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Case-sensitive host: a path that only matches the root in lower case is outside it.
            Assert.Throws<RojoException>(() => RojoPathGuard.ResolveProjectFile(_root, Path.Combine(_root.ToUpperInvariant(), "default.project.json")));
        }
    }

    [Fact]
    public void A_symlink_at_the_project_file_itself_is_refused()
    {
        string link = Path.Combine(_root, "linked.project.json");
        Assert.SkipUnless(TryFileLink(link, Path.Combine(_outside, "secret.txt")), "Symbolic links are not available here.");

        Assert.Equal(RojoErrorKind.PathOutsideRoot, Assert.Throws<RojoException>(() => RojoPathGuard.ResolveProjectFile(_root, "linked.project.json")).Kind);
    }

    [Fact]
    public void A_dangling_symlink_at_the_output_path_is_refused()
    {
        // rojo would create the target of the link, writing outside the root.
        string link = Path.Combine(_root, "out.rbxlx");
        Assert.SkipUnless(TryFileLink(link, Path.Combine(_outside, "does-not-exist.rbxlx")), "Symbolic links are not available here.");

        Assert.Equal(RojoErrorKind.PathOutsideRoot, Assert.Throws<RojoException>(() => RojoPathGuard.ResolveOutputFile(_root, "out.rbxlx")).Kind);
    }

    [Fact]
    public void A_symlinked_directory_in_the_output_path_is_refused()
    {
        Assert.SkipUnless(TryDirectoryLink(Path.Combine(_root, "dist"), _outside), "Symbolic links are not available here.");

        Assert.Throws<RojoException>(() => RojoPathGuard.ResolveOutputFile(_root, "dist/out.rbxlx"));
    }

    [Theory]
    [InlineData("{ \"tree\": { \"$className\": \"DataModel\", \"X\": { \"$path\": \"../outside\" } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": \"src/../../outside/secret.txt\" } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": \"/etc\" } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": \"C:\\\\Windows\" } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": \"\\\\\\\\server\\\\share\" } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": { \"optional\": \"../outside\" } } } }")]
    [InlineData("{ \"tree\": { \"A\": { \"B\": { \"C\": { \"$path\": \"../../../outside\" } } } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": 5 } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": [\"../outside\"] } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": { \"other\": \"src\" } } } }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": \"\" } } }")]
    [InlineData("{ // comment\n \"tree\": { \"X\": { \"$path\": \"../outside\", } }, }")]
    [InlineData("{ \"tree\": { \"X\": { \"$path\": \"src\", \"$path\": \"../outside\" } } }")]
    [InlineData("[ { \"$path\": \"../outside\" } ]")]
    public async Task A_project_file_whose_path_leaves_the_root_is_refused_before_rojo_runs(string project)
    {
        File.WriteAllText(Path.Combine(_root, "hostile.project.json"), project);
        var fake = new FakeProcessRunner();

        await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "hostile.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_path_that_stays_inside_the_root_is_accepted()
    {
        File.WriteAllText(Path.Combine(_root, "ok.project.json"), "{ \"tree\": { \"S\": { \"$path\": \"src\" }, \"T\": { \"$path\": { \"optional\": \"src/../src\" } } } }");
        var fake = new FakeProcessRunner();

        await RunnerWith(fake).BuildAsync(_root, "ok.project.json", "out.rbxlx", TestContext.Current.CancellationToken);

        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task A_project_file_in_a_subfolder_resolves_paths_from_its_own_folder()
    {
        // "../.." from src/ climbs out of the root even though ".." alone would not.
        File.WriteAllText(Path.Combine(_root, "src", "sub.project.json"), "{ \"tree\": { \"X\": { \"$path\": \"../../outside\" } } }");
        var fake = new FakeProcessRunner();

        await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "src/sub.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_nested_project_file_in_a_referenced_folder_is_validated_too()
    {
        // rojo reads default.project.json inside a folder named by $path; its own $path must be checked.
        Directory.CreateDirectory(Path.Combine(_root, "src", "pkg"));
        File.WriteAllText(Path.Combine(_root, "src", "pkg", "default.project.json"), "{ \"tree\": { \"X\": { \"$path\": \"../../../outside\" } } }");
        File.WriteAllText(Path.Combine(_root, "outer.project.json"), "{ \"tree\": { \"S\": { \"$path\": \"src\" } } }");
        var fake = new FakeProcessRunner();

        await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "outer.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_symlink_inside_a_referenced_folder_is_refused()
    {
        Assert.SkipUnless(TryDirectoryLink(Path.Combine(_root, "src", "escape"), _outside), "Symbolic links are not available here.");
        File.WriteAllText(Path.Combine(_root, "outer.project.json"), "{ \"tree\": { \"S\": { \"$path\": \"src\" } } }");
        var fake = new FakeProcessRunner();

        await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "outer.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_path_through_a_symlink_to_the_outside_is_refused()
    {
        Assert.SkipUnless(TryDirectoryLink(Path.Combine(_root, "link"), _outside), "Symbolic links are not available here.");
        File.WriteAllText(Path.Combine(_root, "outer.project.json"), "{ \"tree\": { \"S\": { \"$path\": \"link/secret.txt\" } } }");
        var fake = new FakeProcessRunner();

        await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "outer.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_project_file_with_unbounded_nesting_is_refused()
    {
        string deep = new string('[', 5000) + new string(']', 5000);
        File.WriteAllText(Path.Combine(_root, "deep.project.json"), "{ \"tree\": " + deep + " }");
        var fake = new FakeProcessRunner();

        var error = await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "deep.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.InvalidProjectFile, error.Kind);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task An_oversize_project_file_is_refused_without_being_read_whole()
    {
        File.WriteAllText(Path.Combine(_root, "big.project.json"), "{ \"name\": \"" + new string('a', RojoProjectValidator.MaxProjectFileBytes + 1) + "\" }");
        var fake = new FakeProcessRunner();

        var error = await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "big.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.InvalidProjectFile, error.Kind);
    }

    [Fact]
    public async Task A_project_file_that_is_not_json_is_refused()
    {
        File.WriteAllText(Path.Combine(_root, "junk.project.json"), "not json at all \0\0");
        var fake = new FakeProcessRunner();

        var error = await Assert.ThrowsAsync<RojoException>(() => RunnerWith(fake).BuildAsync(_root, "junk.project.json", "out.rbxlx", TestContext.Current.CancellationToken));

        Assert.Equal(RojoErrorKind.InvalidProjectFile, error.Kind);
    }

    private static bool TryFileLink(string link, string target) => TryLink(() => File.CreateSymbolicLink(link, target));

    private static bool TryDirectoryLink(string link, string target) => TryLink(() => Directory.CreateSymbolicLink(link, target));

    private static bool TryLink(Action create)
    {
        try
        {
            create();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
