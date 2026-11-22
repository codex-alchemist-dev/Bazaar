using Bazaar.Server.Luau;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau.Tests;

/// <summary>
/// Runs the real luau-analyze, selene and stylua against fixtures. Each test skips at runtime when its binary is not on PATH,
/// so CI without the tools stays green.
/// </summary>
public class RealBinaryTests
{
    [Fact]
    public async Task Analyze_reports_a_type_error_in_a_real_file()
    {
        RequireOnPath("luau-analyze");
        using var project = new TempProject();
        File.WriteAllText(Path.Combine(project.Root, "scripts", "Bad.lua"), "local n: number = \"text\"\n");

        LuauCheckResult result = await new LuauChecker(new LuauToolOptions(), new SystemLuauToolRunner())
            .CheckAsync(new LuauCheckRequest(project.Root, ["scripts/Bad.lua"], [LuauToolKind.Analyze]), TestContext.Current.CancellationToken);

        Assert.Equal(LuauToolStatus.Findings, result.Tools[0].Status);
    }

    [Fact]
    public async Task Selene_reports_an_unused_local_in_a_real_file()
    {
        RequireOnPath("selene");
        using var project = new TempProject();
        File.WriteAllText(Path.Combine(project.Root, "scripts", "Unused.lua"), "local unused = 1\n");

        LuauCheckResult result = await new LuauChecker(new LuauToolOptions(), new SystemLuauToolRunner())
            .CheckAsync(new LuauCheckRequest(project.Root, ["scripts/Unused.lua"], [LuauToolKind.Selene]), TestContext.Current.CancellationToken);

        Assert.Equal(LuauToolStatus.Findings, result.Tools[0].Status);
    }

    [Fact]
    public async Task StyLua_flags_an_unformatted_real_file_and_passes_a_formatted_one()
    {
        RequireOnPath("stylua");
        using var project = new TempProject();
        File.WriteAllText(Path.Combine(project.Root, "scripts", "Messy.lua"), "local   x=1\n");
        File.WriteAllText(Path.Combine(project.Root, "scripts", "Tidy.lua"), "local x = 1\n");
        var checker = new LuauChecker(new LuauToolOptions(), new SystemLuauToolRunner());

        LuauCheckResult messy = await checker.CheckAsync(new LuauCheckRequest(project.Root, ["scripts/Messy.lua"], [LuauToolKind.StyLuaCheck]), TestContext.Current.CancellationToken);
        LuauCheckResult tidy = await checker.CheckAsync(new LuauCheckRequest(project.Root, ["scripts/Tidy.lua"], [LuauToolKind.StyLuaCheck]), TestContext.Current.CancellationToken);

        Assert.Equal(LuauToolStatus.Findings, messy.Tools[0].Status);
        Assert.Equal(LuauToolStatus.Passed, tidy.Tools[0].Status);
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
