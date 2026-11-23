namespace Bazaar.Client.Windowing.Tests;

/// <summary>Launch routing: titles, ordering, and the untrusted-path rules. Pure: no fakes needed.</summary>
public sealed class LaunchRouterTests
{
    private static WindowLaunch Paths(params string?[] paths) => new(paths.Select(p => p!).ToList(), OpenTerminal: false);

    [Fact]
    public void Empty_launch_with_main_window_returns_nothing()
    {
        var windows = LaunchRouter.Route(new WindowLaunch([], OpenTerminal: false), mainWindowExists: true);

        Assert.Empty(windows);
    }

    [Fact]
    public void Empty_launch_without_main_window_opens_main()
    {
        var windows = LaunchRouter.Route(new WindowLaunch([], OpenTerminal: false), mainWindowExists: false);

        var main = Assert.Single(windows);
        Assert.Equal(WindowKind.Main, main.Kind);
        Assert.Equal(LaunchRouter.MainTitle, main.Title);
    }

    [Fact]
    public void Absolute_path_becomes_editor_with_file_name_as_title()
    {
        var windows = LaunchRouter.Route(Paths(@"C:\work\src\notes.md"), mainWindowExists: true);

        var editor = Assert.Single(windows);
        Assert.Equal(WindowKind.Editor, editor.Kind);
        Assert.Equal("notes.md", editor.Title);
        Assert.Equal(@"C:\work\src\notes.md", editor.Path);
        Assert.Null(editor.Bounds);
    }

    [Fact]
    public void Path_is_kept_in_original_case()
    {
        var windows = LaunchRouter.Route(Paths(@"C:\Work\README.TXT"), mainWindowExists: true);

        Assert.Equal(@"C:\Work\README.TXT", Assert.Single(windows).Path);
    }

    [Fact]
    public void Terminal_only_opens_one_terminal_window()
    {
        var windows = LaunchRouter.Route(new WindowLaunch([], OpenTerminal: true), mainWindowExists: true);

        var terminal = Assert.Single(windows);
        Assert.Equal(WindowKind.Terminal, terminal.Kind);
        Assert.Equal(LaunchRouter.TerminalTitle, terminal.Title);
        Assert.Null(terminal.Path);
    }

    [Fact]
    public void Editors_come_first_then_terminal_in_request_order()
    {
        var windows = LaunchRouter.Route(
            new WindowLaunch([@"C:\a\one.txt", @"C:\a\two.txt"], OpenTerminal: true),
            mainWindowExists: true);

        Assert.Collection(
            windows,
            w => Assert.Equal("one.txt", w.Title),
            w => Assert.Equal("two.txt", w.Title),
            w => Assert.Equal(WindowKind.Terminal, w.Kind));
    }

    [Theory]
    [InlineData("notes.md")]
    [InlineData(@"..\secret.txt")]
    [InlineData("docs/readme.md")]
    [InlineData(@"C:relative.txt")]
    [InlineData(@"\rooted-but-not-qualified.txt")]
    public void Relative_paths_are_dropped(string path)
    {
        var windows = LaunchRouter.Route(Paths(path), mainWindowExists: true);

        Assert.Empty(windows);
    }

    [Fact]
    public void UNC_path_is_allowed()
    {
        var windows = LaunchRouter.Route(Paths(@"\\server\share\a.txt"), mainWindowExists: true);

        var editor = Assert.Single(windows);
        Assert.Equal(@"\\server\share\a.txt", editor.Path);
        Assert.Equal("a.txt", editor.Title);
    }

    [Theory]
    [InlineData(@"\\.\C:\Windows\System32\config")]
    [InlineData(@"\\?\C:\secret.txt")]
    [InlineData(@"\\?\UNC\server\share\a.txt")]
    [InlineData(@"//./C:/secret.txt")]
    [InlineData(@"//?/C:/secret.txt")]
    public void Device_prefixes_are_dropped(string path)
    {
        var windows = LaunchRouter.Route(Paths(path), mainWindowExists: true);

        Assert.Empty(windows);
    }

    [Theory]
    [InlineData("C:\\a\0b.txt")]
    [InlineData("C:\\a\nb.txt")]
    [InlineData("C:\\a\rb.txt")]
    [InlineData("C:\\a\tb.txt")]
    [InlineData("C:\\a\u0007b.txt")]
    [InlineData("C:\\a\u007Fb.txt")]
    public void Control_characters_are_dropped(string path)
    {
        var windows = LaunchRouter.Route(Paths(path), mainWindowExists: true);

        Assert.Empty(windows);
    }

    [Fact]
    public void Path_of_exactly_max_length_is_kept()
    {
        string path = @"C:\" + new string('a', LaunchRouter.MaxPathLength - 3);

        var windows = LaunchRouter.Route(Paths(path), mainWindowExists: true);

        Assert.Equal(path, Assert.Single(windows).Path);
    }

    [Fact]
    public void Path_one_char_over_max_length_is_dropped()
    {
        string path = @"C:\" + new string('a', LaunchRouter.MaxPathLength - 2);

        var windows = LaunchRouter.Route(Paths(path), mainWindowExists: true);

        Assert.Empty(windows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Null_or_blank_paths_are_dropped(string? path)
    {
        var windows = LaunchRouter.Route(Paths(path), mainWindowExists: true);

        Assert.Empty(windows);
    }

    [Fact]
    public void Directory_path_without_file_name_is_dropped()
    {
        var windows = LaunchRouter.Route(Paths(@"C:\work\dir\"), mainWindowExists: true);

        Assert.Empty(windows);
    }

    [Fact]
    public void Duplicates_differing_only_in_case_open_once()
    {
        var windows = LaunchRouter.Route(Paths(@"C:\A\X.txt", @"c:\a\x.TXT", @"C:\A\X.txt"), mainWindowExists: true);

        var editor = Assert.Single(windows);
        Assert.Equal(@"C:\A\X.txt", editor.Path);
    }

    [Fact]
    public void Seventeen_valid_paths_open_sixteen_editors()
    {
        var paths = Enumerable.Range(1, 17).Select(i => $@"C:\files\f{i}.txt").ToList();

        var windows = LaunchRouter.Route(new WindowLaunch(paths, OpenTerminal: false), mainWindowExists: true);

        Assert.Equal(LaunchRouter.MaxEditorWindows, windows.Count);
        Assert.All(windows, w => Assert.Equal(WindowKind.Editor, w.Kind));
        Assert.Equal("f16.txt", windows[^1].Title);
    }

    [Fact]
    public void Cap_applies_to_editors_and_terminal_still_opens()
    {
        var paths = Enumerable.Range(1, 17).Select(i => $@"C:\files\f{i}.txt").ToList();

        var windows = LaunchRouter.Route(new WindowLaunch(paths, OpenTerminal: true), mainWindowExists: true);

        Assert.Equal(LaunchRouter.MaxEditorWindows + 1, windows.Count);
        Assert.Equal(WindowKind.Terminal, windows[^1].Kind);
    }

    [Fact]
    public void Dropped_paths_do_not_count_toward_the_cap()
    {
        var paths = new List<string> { "relative.txt", "\\\\.\\C:\\x.txt", "bad\0.txt" };
        paths.AddRange(Enumerable.Range(1, LaunchRouter.MaxEditorWindows).Select(i => $@"C:\files\f{i}.txt"));

        var windows = LaunchRouter.Route(new WindowLaunch(paths, OpenTerminal: false), mainWindowExists: true);

        Assert.Equal(LaunchRouter.MaxEditorWindows, windows.Count);
        Assert.Equal("f1.txt", windows[0].Title);
    }

    [Fact]
    public void Duplicates_do_not_count_toward_the_cap()
    {
        var paths = new List<string> { @"C:\same.txt", @"c:\SAME.txt" };
        paths.AddRange(Enumerable.Range(1, LaunchRouter.MaxEditorWindows - 1).Select(i => $@"C:\files\f{i}.txt"));

        var windows = LaunchRouter.Route(new WindowLaunch(paths, OpenTerminal: false), mainWindowExists: true);

        Assert.Equal(LaunchRouter.MaxEditorWindows, windows.Count);
    }

    [Fact]
    public void Only_invalid_paths_with_terminal_opens_just_the_terminal()
    {
        var windows = LaunchRouter.Route(
            new WindowLaunch(["relative.txt", "\\\\?\\C:\\x", "  "], OpenTerminal: true),
            mainWindowExists: true);

        var terminal = Assert.Single(windows);
        Assert.Equal(WindowKind.Terminal, terminal.Kind);
    }

    [Fact]
    public void Missing_main_window_is_listed_first_before_requested_windows()
    {
        var windows = LaunchRouter.Route(
            new WindowLaunch([@"C:\a.txt"], OpenTerminal: true),
            mainWindowExists: false);

        Assert.Collection(
            windows,
            w => Assert.Equal(WindowKind.Main, w.Kind),
            w => Assert.Equal(WindowKind.Editor, w.Kind),
            w => Assert.Equal(WindowKind.Terminal, w.Kind));
    }

    [Fact]
    public void Route_is_repeatable_and_does_not_keep_state_between_calls()
    {
        var launch = new WindowLaunch([@"C:\a.txt"], OpenTerminal: false);

        var first = LaunchRouter.Route(launch, mainWindowExists: true);
        var second = LaunchRouter.Route(launch, mainWindowExists: true);

        Assert.Equal(first, second);
    }
}
