using Bazaar.Server.Godot.Abstractions;

namespace Bazaar.Server.Godot.Tests;

/// <summary>
/// Runs the real Godot against a fixture project. Each test skips at runtime when <c>godot</c> is not on PATH, so CI without
/// the engine stays green. The canary test fails the build if a real binary ignores the check flags.
/// </summary>
public class RealBinaryTests
{
    [Fact]
    public async Task Real_godot_passes_the_canary()
    {
        RequireOnPath("godot");
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), new SystemGodotProcessRunner());

        GodotAvailability availability = await adapter.EnsureAvailableAsync(TestContext.Current.CancellationToken);

        Assert.True(availability.Available, availability.Reason);
    }

    [Fact]
    public async Task Real_godot_check_only_fails_a_broken_script_and_passes_a_good_one()
    {
        RequireOnPath("godot");
        using var project = new TempGodotProject();
        File.WriteAllText(project.Path_("scripts/bad.gd"), "extends Node\nfunc _ready(:\n");
        File.WriteAllText(project.Path_("scripts/good.gd"), "extends Node\n\nfunc _ready() -> void:\n\tpass\n");
        using var adapter = new GodotAdapter(new GodotAdapterOptions(), new SystemGodotProcessRunner());
        CancellationToken ct = TestContext.Current.CancellationToken;

        GodotRunResult bad = await adapter.RunAsync(new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: "scripts/bad.gd"), ct);
        GodotRunResult good = await adapter.RunAsync(new GodotCommandRequest(project.Root, GodotCommandKind.CheckScript, Script: "scripts/good.gd"), ct);

        Assert.Equal(GodotRunStatus.Failed, bad.Status);
        Assert.Equal(GodotRunStatus.Succeeded, good.Status);
    }

    private static void RequireOnPath(string binary)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            Assert.Skip($"{binary} is not on PATH.");
        }

        string[] names = OperatingSystem.IsWindows() ? [binary + ".exe", binary] : [binary];
        bool found = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => names.Any(name => File.Exists(Path.Combine(dir, name))));
        if (!found)
        {
            Assert.Skip($"{binary} is not on PATH.");
        }
    }
}
