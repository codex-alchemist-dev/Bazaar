using System.ComponentModel;
using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot.Tests;

public class BuildCheckTests
{
    private const string _features = "config_version=5\n\n[application]\nconfig/features=PackedStringArray(\"4.7\", \"C#\")\n";

    [Theory]
    [InlineData("4.7.2.stable.official.0123abcde", 4, 7, 2, "stable", false, false)]
    [InlineData("4.7.2.stable.mono.official.0123abcde", 4, 7, 2, "stable", true, false)]
    [InlineData("4.7.stable.official.0123abcde", 4, 7, 0, "stable", false, false)]
    [InlineData("4.8.dev3.official.0123abcde", 4, 8, 0, "dev3", false, false)]
    [InlineData("4.7.2.stable.template_release", 4, 7, 2, "stable", false, true)]
    public void Version_strings_parse(string text, int major, int minor, int patch, string status, bool mono, bool template)
    {
        GodotVersion? v = GodotVersion.Parse("\n" + text + "\r\n");

        Assert.NotNull(v);
        Assert.Equal((major, minor, patch, status, mono, template), (v.Major, v.Minor, v.Patch, v.Status, v.IsMono, v.IsTemplate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Godot is great")]
    [InlineData("4.7")]
    [InlineData("4.7.2")]
    public void Unrecognised_version_strings_do_not_parse(string text)
    {
        Assert.Null(GodotVersion.Parse(text));
    }

    [Fact]
    public async Task Matching_editor_build_passes()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.official.abc\n"));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Passed, result.Status);
        Assert.Equal("4.7.2.stable", result.Version);
        Assert.True(result.IsEditorBuild);
        Assert.Equal(GodotBuildVariant.GdScript, result.BinaryVariant);
        Assert.Equal(GodotBuildVariant.GdScript, result.ProjectVariant);
        Assert.Equal(["--version"], runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task Missing_binary_is_unavailable_with_a_reason()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => throw new Win32Exception("No such file"));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Unavailable, result.Status);
        Assert.Contains("Could not start", Assert.Single(result.Failures));
    }

    [Fact]
    public async Task Template_build_fails()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.template_release\n"));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Failed, result.Status);
        Assert.False(result.IsEditorBuild);
        Assert.Contains(result.Failures, f => f.Contains("template", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    public async Task Version_failures_exit_codes_and_garbage_fail(int exit)
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => exit < 0 ? FakeGodotRunner.Ok("hello\n") : FakeGodotRunner.Exit(exit));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Failed, result.Status);
        Assert.Null(result.Version);
    }

    [Fact]
    public async Task Version_timeout_fails()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => Task.FromResult(new Bazaar.Server.Engines.Abstractions.ProcessResult(0, string.Empty, string.Empty, TimedOut: true)));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Mono_binary_against_gdscript_setting_fails()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.mono.official.abc\n"));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Failed, result.Status);
        Assert.Equal(GodotBuildVariant.Dotnet, result.BinaryVariant);
    }

    [Fact]
    public async Task Dotnet_project_detected_from_features_fails_a_gdscript_setting()
    {
        using var project = new TempGodotProject(_features);
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.official.abc\n"));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Failed, result.Status);
        Assert.Equal(GodotBuildVariant.Dotnet, result.ProjectVariant);
    }

    [Theory]
    [InlineData("App.csproj")]
    [InlineData("App.sln")]
    public async Task Dotnet_project_detected_from_a_solution_file_and_matching_setting_passes(string file)
    {
        using var project = new TempGodotProject();
        File.WriteAllText(project.Path_(file), "<Project />");
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.mono.official.abc\n"));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions { BuildVariant = GodotBuildVariant.Dotnet }, runner)
            .BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Passed, result.Status);
        Assert.Equal(GodotBuildVariant.Dotnet, result.ProjectVariant);
    }

    [Fact]
    public async Task Different_patch_warns_when_following_maintenance_and_fails_otherwise()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.3.stable.official.abc\n"));

        GodotBuildCheckResult follow = await new GodotAdapter(new GodotAdapterOptions { FollowMaintenance = true }, runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);
        GodotBuildCheckResult strict = await new GodotAdapter(new GodotAdapterOptions { FollowMaintenance = false }, runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Warning, follow.Status);
        Assert.Single(follow.Warnings);
        Assert.Equal(GodotBuildCheckStatus.Failed, strict.Status);
    }

    [Fact]
    public async Task Different_minor_always_fails()
    {
        using var project = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.8.0.stable.official.abc\n"));

        GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions { FollowMaintenance = true }, runner).BuildCheckAsync(project.Root, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Project_without_project_godot_fails()
    {
        string root = Path.Combine(Path.GetTempPath(), "bazaar-godot-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.official.abc\n"));

            GodotBuildCheckResult result = await new GodotAdapter(new GodotAdapterOptions(), runner).BuildCheckAsync(root, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(GodotBuildCheckStatus.Failed, result.Status);
            Assert.Null(result.ProjectVariant);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Export_preset_needs_to_exist_and_templates_to_be_installed()
    {
        using var project = new TempGodotProject();
        project.WritePresets("Linux Release");
        using var templates = new TempGodotProject();
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.official.abc\n"));
        var options = new GodotAdapterOptions { ExportTemplatesRoot = templates.Root };
        var adapter = new GodotAdapter(options, runner);
        CancellationToken ct = TestContext.Current.CancellationToken;

        GodotBuildCheckResult noTemplates = await adapter.BuildCheckAsync(project.Root, "Linux Release", ct);
        Directory.CreateDirectory(Path.Combine(templates.Root, "4.7.2.stable"));
        File.WriteAllText(Path.Combine(templates.Root, "4.7.2.stable", "version.txt"), "4.7.2.stable");
        GodotBuildCheckResult withTemplates = await adapter.BuildCheckAsync(project.Root, "Linux Release", ct);
        GodotBuildCheckResult unknownPreset = await adapter.BuildCheckAsync(project.Root, "Nope", ct);
        GodotBuildCheckResult unsafePreset = await adapter.BuildCheckAsync(project.Root, "x\"; rm -rf", ct);

        Assert.Equal(GodotBuildCheckStatus.Failed, noTemplates.Status);
        Assert.Contains(noTemplates.Failures, f => f.Contains("templates", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(GodotBuildCheckStatus.Passed, withTemplates.Status);
        Assert.Equal(GodotBuildCheckStatus.Failed, unknownPreset.Status);
        Assert.Equal(GodotBuildCheckStatus.Failed, unsafePreset.Status);
    }

    [Fact]
    public async Task Mono_export_templates_live_in_a_mono_folder()
    {
        using var project = new TempGodotProject(_features);
        project.WritePresets("Linux Release");
        using var templates = new TempGodotProject();
        Directory.CreateDirectory(Path.Combine(templates.Root, "4.7.2.stable.mono"));
        File.WriteAllText(Path.Combine(templates.Root, "4.7.2.stable.mono", "version.txt"), "x");
        var runner = new FakeGodotRunner((_, _, _) => FakeGodotRunner.Ok("4.7.2.stable.mono.official.abc\n"));

        GodotBuildCheckResult result = await new GodotAdapter(
            new GodotAdapterOptions { BuildVariant = GodotBuildVariant.Dotnet, ExportTemplatesRoot = templates.Root },
            runner).BuildCheckAsync(project.Root, "Linux Release", TestContext.Current.CancellationToken);

        Assert.Equal(GodotBuildCheckStatus.Passed, result.Status);
    }

    [Fact]
    public void Preset_names_are_read_from_preset_sections_only()
    {
        using var project = new TempGodotProject();
        project.WritePresets("Windows Desktop", "Linux");

        Assert.Equal(["Windows Desktop", "Linux"], GodotProjectInspector.ReadPresetNames(project.Root));
    }

    [Fact]
    public void Bad_options_are_rejected()
    {
        var runner = new FakeGodotRunner();

        Assert.Throws<ArgumentException>(() => new GodotAdapter(new GodotAdapterOptions { VersionPin = "latest" }, runner));
        Assert.Throws<ArgumentException>(() => new GodotAdapter(new GodotAdapterOptions { CommandTimeoutMinutes = 0 }, runner));
        Assert.Throws<ArgumentException>(() => new GodotAdapter(new GodotAdapterOptions { CommandTimeoutMinutes = 121 }, runner));
        Assert.Throws<ArgumentException>(() => new GodotAdapter(new GodotAdapterOptions { BinaryPath = " " }, runner));
        Assert.Throws<ArgumentException>(() => new GodotAdapter(new GodotAdapterOptions { MaxOutputCharacters = 0 }, runner));
    }
}
