using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot.Tests;

public class CommandBuilderTests
{
    private static IReadOnlyList<string> Build(TempGodotProject project, GodotCommandRequest request) =>
        GodotCommandBuilder.Build(GodotPathGuard.RequireProjectRoot(project.Root), request);

    [Fact]
    public void Import_arguments()
    {
        using var project = new TempGodotProject();

        Assert.Equal(["--headless", "--import"], Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.Import)));
    }

    [Fact]
    public void Check_script_arguments_use_forward_slashes()
    {
        using var project = new TempGodotProject();

        Assert.Equal(
            ["--headless", "--check-only", "--script", "scripts/main.gd"],
            Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: Path.Combine("scripts", "main.gd"))));
    }

    [Fact]
    public void Run_script_arguments_and_res_prefix()
    {
        using var project = new TempGodotProject();

        Assert.Equal(
            ["--headless", "--script", "scripts/run_tests.gd"],
            Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.RunScript, Script: "res://scripts/run_tests.gd")));
    }

    [Fact]
    public void Export_arguments_keep_the_preset_as_one_argument()
    {
        using var project = new TempGodotProject();
        project.WritePresets("Linux Release (x64)");

        IReadOnlyList<string> args = Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.Export, Preset: "Linux Release (x64)", OutputDirectory: "build"));

        Assert.Equal(["--headless", "--export-release", "Linux Release (x64)", "build"], args);
    }

    [Theory]
    [InlineData("../outside.gd")]
    [InlineData("scripts/../../outside.gd")]
    [InlineData("res://../outside.gd")]
    public void Traversal_is_rejected(string script)
    {
        using var project = new TempGodotProject();

        Assert.Throws<GodotRequestRejectedException>(() => Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: script)));
    }

    [Fact]
    public void Absolute_paths_outside_the_root_are_rejected()
    {
        using var project = new TempGodotProject();
        using var other = new TempGodotProject();

        Assert.Throws<GodotRequestRejectedException>(() =>
            Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: other.Path_("scripts/main.gd"))));
    }

    [Fact]
    public void Absolute_path_inside_the_root_is_accepted_and_made_relative()
    {
        using var project = new TempGodotProject();

        IReadOnlyList<string> args = Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: project.Path_("scripts/main.gd")));

        Assert.Equal("scripts/main.gd", args[^1]);
    }

    [Fact]
    public void Symlinked_file_is_rejected()
    {
        using var project = new TempGodotProject();
        using var other = new TempGodotProject();
        string link = project.Path_("scripts/link.gd");
        if (!TryLinkFile(link, other.Path_("scripts/main.gd")))
        {
            Assert.Skip("Symbolic links cannot be created here.");
        }

        Assert.Throws<GodotRequestRejectedException>(() => Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: "scripts/link.gd")));
    }

    [Fact]
    public void Symlinked_directory_is_rejected_for_scripts_and_export_output()
    {
        using var project = new TempGodotProject();
        using var other = new TempGodotProject();
        project.WritePresets("Linux");
        string link = project.Path_("linked");
        if (!TryLinkDirectory(link, other.Root))
        {
            Assert.Skip("Symbolic links cannot be created here.");
        }

        Assert.Throws<GodotRequestRejectedException>(() => Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: "linked/scripts/main.gd")));
        Assert.Throws<GodotRequestRejectedException>(() => Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.Export, Preset: "Linux", OutputDirectory: "linked")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("scripts/missing.gd")]
    [InlineData("scripts")]
    [InlineData("project.godot")]
    public void Missing_empty_or_non_gd_scripts_are_rejected(string? script)
    {
        using var project = new TempGodotProject();

        Assert.Throws<GodotRequestRejectedException>(() => Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.RunScript, Script: script)));
    }

    [Fact]
    public void A_script_starting_with_a_dash_is_rejected()
    {
        using var project = new TempGodotProject();
        File.WriteAllText(project.Path_("-evil.gd"), "extends Node\n");

        Assert.Throws<GodotRequestRejectedException>(() => Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.RunScript, Script: "-evil.gd")));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("--path")]
    [InlineData("")]
    [InlineData("Linux; rm -rf /")]
    [InlineData("Linux\"")]
    [InlineData("Linux/X11")]
    [InlineData("Linux\nRelease")]
    [InlineData("Linux\n")]
    public void Export_presets_not_in_the_file_or_with_unsafe_characters_are_rejected(string preset)
    {
        using var project = new TempGodotProject();
        project.WritePresets("Linux");

        Assert.Throws<GodotRequestRejectedException>(() =>
            Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.Export, Preset: preset, OutputDirectory: "build")));
    }

    [Fact]
    public void An_unsafe_preset_name_is_rejected_even_when_the_file_lists_it()
    {
        using var project = new TempGodotProject();
        project.WritePresets("--evil");

        Assert.Throws<GodotRequestRejectedException>(() =>
            Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.Export, Preset: "--evil", OutputDirectory: "build")));
    }

    [Fact]
    public void Export_with_no_presets_file_is_rejected()
    {
        using var project = new TempGodotProject();

        Assert.Throws<GodotRequestRejectedException>(() =>
            Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.Export, Preset: "Linux", OutputDirectory: "build")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing-dir")]
    [InlineData(".")]
    [InlineData("../")]
    [InlineData("scripts/main.gd")]
    public void Export_output_directory_must_be_an_existing_directory_below_the_root(string? output)
    {
        using var project = new TempGodotProject();
        project.WritePresets("Linux");

        Assert.Throws<GodotRequestRejectedException>(() =>
            Build(project, new GodotCommandRequest(project.Root, GodotCommandKind.Export, Preset: "Linux", OutputDirectory: output)));
    }

    [Fact]
    public void Root_without_project_godot_or_missing_is_rejected()
    {
        string missing = Path.Combine(Path.GetTempPath(), "bazaar-godot-none-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<GodotRequestRejectedException>(() => GodotPathGuard.RequireProjectRoot(missing));
        Assert.Throws<GodotRequestRejectedException>(() => GodotPathGuard.RequireProjectRoot(" "));
    }

    private static bool TryLinkFile(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryLinkDirectory(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
