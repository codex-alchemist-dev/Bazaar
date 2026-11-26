using System.ComponentModel;
using System.Text;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau.Tests;

/// <summary>Checker behaviour with a scripted runner: arguments, statuses, path rules, missing binaries, timeouts, cancellation, output cap.</summary>
public class LuauCheckerTests
{
    private static readonly string[] _mainOnly = ["scripts/Main.lua"];
    private static readonly string[] _seleneMain = ["--display-style=Json2", "scripts/Main.lua"];
    private static readonly string[] _styluaMain = ["--check", "scripts/Main.lua"];

    private static LuauCheckRequest Request(TempProject project, params string[] files) =>
        new(project.Root, files, []);

    private static LuauChecker CheckerWith(FakeLuauToolRunner runner, LuauToolOptions? options = null) =>
        new(options ?? new LuauToolOptions(), runner);

    [Fact]
    public async Task Runs_each_tool_with_argument_lists_in_the_project_root()
    {
        using var project = new TempProject();
        var runner = FakeLuauToolRunner.AllClean();
        LuauChecker checker = CheckerWith(runner);

        LuauCheckResult result = await checker.CheckAsync(Request(project, "scripts/Main.lua"), TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Tools.Count);
        Assert.All(result.Tools, report => Assert.Equal(LuauToolStatus.Passed, report.Status));
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal("luau-analyze", runner.Calls[0].FileName);
        Assert.Equal(_mainOnly, runner.Calls[0].Arguments);
        Assert.Equal("selene", runner.Calls[1].FileName);
        Assert.Equal(_seleneMain, runner.Calls[1].Arguments);
        Assert.Equal("stylua", runner.Calls[2].FileName);
        Assert.Equal(_styluaMain, runner.Calls[2].Arguments);
        Assert.All(runner.Calls, call => Assert.Equal(Path.GetFullPath(project.Root), Path.GetFullPath(call.WorkingDirectory)));
        Assert.All(runner.Calls, call => Assert.Equal(TimeSpan.FromMinutes(2), call.Timeout));
    }

    [Fact]
    public async Task Request_tools_limit_which_tools_run_and_binary_paths_come_from_options()
    {
        using var project = new TempProject();
        var runner = FakeLuauToolRunner.AllClean();
        var options = new LuauToolOptions { StyLuaBinaryPath = "/opt/luau/stylua" };
        LuauChecker checker = CheckerWith(runner, options);
        var request = new LuauCheckRequest(project.Root, ["scripts/Util.lua"], [LuauToolKind.StyLuaCheck], TimeSpan.FromSeconds(9));

        LuauCheckResult result = await checker.CheckAsync(request, TestContext.Current.CancellationToken);

        LuauToolReport only = Assert.Single(result.Tools);
        Assert.Equal(LuauToolKind.StyLuaCheck, only.Tool);
        Assert.Equal("/opt/luau/stylua", Assert.Single(runner.Calls).FileName);
        Assert.Equal(TimeSpan.FromSeconds(9), runner.Calls[0].Timeout);
    }

    [Fact]
    public async Task Findings_are_reported_with_their_tool_and_status()
    {
        using var project = new TempProject();
        var runner = new FakeLuauToolRunner((file, _) => Task.FromResult(file switch
        {
            "luau-analyze" => new LuauProcessResult(1, "scripts/Main.lua(1,1): TypeError: bad\n", string.Empty, false),
            "selene" => new LuauProcessResult(1, string.Empty, string.Empty, false),
            _ => new LuauProcessResult(1, "Diff in scripts/Main.lua:\n", string.Empty, false),
        }));
        LuauChecker checker = CheckerWith(runner);

        LuauCheckResult result = await checker.CheckAsync(Request(project, "scripts/Main.lua"), TestContext.Current.CancellationToken);

        Assert.Equal(LuauToolStatus.Findings, result.Tools[0].Status);
        Assert.Equal(LuauToolStatus.Error, result.Tools[1].Status);
        Assert.Equal(LuauToolStatus.Findings, result.Tools[2].Status);
        Assert.Equal(2, result.Diagnostics.Count);
        Assert.Equal(LuauToolKind.Analyze, result.Diagnostics[0].Tool);
        Assert.Equal(LuauToolKind.StyLuaCheck, result.Diagnostics[1].Tool);
        Assert.Equal(1, result.Tools[1].ExitCode);
        Assert.Contains("selene", result.Tools[1].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_binary_is_unavailable_and_the_other_tools_still_run()
    {
        using var project = new TempProject();
        var runner = new FakeLuauToolRunner((file, _) => file == "luau-analyze"
            ? throw new Win32Exception(2, "No such file or directory")
            : Task.FromResult(new LuauProcessResult(0, string.Empty, string.Empty, false)));
        LuauChecker checker = CheckerWith(runner);

        LuauCheckResult result = await checker.CheckAsync(Request(project, "scripts/Main.lua"), TestContext.Current.CancellationToken);

        Assert.Equal(LuauToolStatus.Unavailable, result.Tools[0].Status);
        Assert.Null(result.Tools[0].ExitCode);
        Assert.Contains("No such file", result.Tools[0].Reason, StringComparison.Ordinal);
        Assert.Equal(LuauToolStatus.Passed, result.Tools[1].Status);
        Assert.Equal(LuauToolStatus.Passed, result.Tools[2].Status);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Fact]
    public async Task Missing_file_on_disk_is_rejected_before_any_tool_runs()
    {
        using var project = new TempProject();
        var runner = FakeLuauToolRunner.AllClean();

        await Assert.ThrowsAsync<LuauRequestRejectedException>(() => CheckerWith(runner).CheckAsync(Request(project, "scripts/Missing.lua"), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Timed_out_tool_reports_timed_out_without_diagnostics()
    {
        using var project = new TempProject();
        var runner = new FakeLuauToolRunner((file, _) => Task.FromResult(file == "selene"
            ? new LuauProcessResult(-1, "{\"code\":\"x\",\"level\":\"error\",\"message\":\"m\"}", string.Empty, TimedOut: true)
            : new LuauProcessResult(0, string.Empty, string.Empty, false)));
        LuauChecker checker = CheckerWith(runner);

        LuauCheckResult result = await checker.CheckAsync(Request(project, "scripts/Main.lua"), TestContext.Current.CancellationToken);

        Assert.Equal(LuauToolStatus.TimedOut, result.Tools[1].Status);
        Assert.Null(result.Tools[1].ExitCode);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuauToolStatus.Passed, result.Tools[2].Status);
    }

    [Fact]
    public async Task Cancellation_propagates_as_operation_canceled()
    {
        using var project = new TempProject();
        using var cancel = new CancellationTokenSource();
        var runner = new FakeLuauToolRunner(async (_, ct) =>
        {
            cancel.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new LuauProcessResult(0, string.Empty, string.Empty, false);
        });
        LuauChecker checker = CheckerWith(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checker.CheckAsync(Request(project, "scripts/Main.lua"), cancel.Token));
    }

    [Fact]
    public async Task Output_beyond_the_cap_is_cut_marked_and_still_parsed()
    {
        using var project = new TempProject();
        var output = new StringBuilder("scripts/Main.lua(1,1): TypeError: first\n");
        output.Append('x', 5000);
        var runner = new FakeLuauToolRunner((file, _) => Task.FromResult(file == "luau-analyze"
            ? new LuauProcessResult(1, output.ToString(), string.Empty, false)
            : new LuauProcessResult(0, string.Empty, string.Empty, false)));
        LuauChecker checker = CheckerWith(runner, new LuauToolOptions { MaxOutputCharacters = 1000 });

        LuauCheckResult result = await checker.CheckAsync(Request(project, "scripts/Main.lua"), TestContext.Current.CancellationToken);

        Assert.True(result.Tools[0].OutputTruncated);
        Assert.False(result.Tools[1].OutputTruncated);
        Assert.Equal(LuauToolStatus.Findings, result.Tools[0].Status);
        Assert.Equal("first", result.Diagnostics[0].Message);
    }

    [Fact]
    public async Task Output_within_the_cap_is_not_marked()
    {
        using var project = new TempProject();
        LuauChecker checker = CheckerWith(FakeLuauToolRunner.AllClean(), new LuauToolOptions { MaxOutputCharacters = 1000 });

        LuauCheckResult result = await checker.CheckAsync(Request(project, "scripts/Main.lua"), TestContext.Current.CancellationToken);

        Assert.All(result.Tools, report => Assert.False(report.OutputTruncated));
    }

    [Theory]
    [InlineData("../outside.lua")]
    [InlineData("scripts/../../outside.lua")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task Paths_that_escape_the_root_are_rejected_before_any_tool_runs(string path)
    {
        using var project = new TempProject();
        var runner = FakeLuauToolRunner.AllClean();

        await Assert.ThrowsAsync<LuauRequestRejectedException>(() => CheckerWith(runner).CheckAsync(Request(project, path), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_symbolic_link_below_the_root_is_rejected()
    {
        using var project = new TempProject();
        using var outside = new TempProject();
        string link = Path.Combine(project.Root, "linked");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(outside.Root, "scripts"));
        }
        catch (IOException ex)
        {
            Assert.Skip($"Cannot create a symbolic link on this host: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Assert.Skip($"Cannot create a symbolic link on this host: {ex.Message}");
        }

        var runner = FakeLuauToolRunner.AllClean();

        await Assert.ThrowsAsync<LuauRequestRejectedException>(() => CheckerWith(runner).CheckAsync(Request(project, "linked/Main.lua"), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_missing_project_root_is_rejected()
    {
        var runner = FakeLuauToolRunner.AllClean();
        var request = new LuauCheckRequest(Path.Combine(Path.GetTempPath(), "bazaar-luau-absent-" + Guid.NewGuid().ToString("N")), ["a.lua"], []);

        await Assert.ThrowsAsync<LuauRequestRejectedException>(() => CheckerWith(runner).CheckAsync(request, TestContext.Current.CancellationToken));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Duplicate_files_and_tools_are_collapsed()
    {
        using var project = new TempProject();
        var runner = FakeLuauToolRunner.AllClean();
        var request = new LuauCheckRequest(project.Root, ["scripts/Main.lua", "scripts/../scripts/Main.lua"], [LuauToolKind.Analyze, LuauToolKind.Analyze]);

        LuauCheckResult result = await CheckerWith(runner).CheckAsync(request, TestContext.Current.CancellationToken);

        Assert.Single(result.Tools);
        Assert.Equal(_mainOnly, runner.Calls[0].Arguments);
    }

    [Fact]
    public void Options_with_empty_binary_path_or_zero_timeout_are_rejected()
    {
        var runner = FakeLuauToolRunner.AllClean();

        Assert.Throws<ArgumentException>(() => new LuauChecker(new LuauToolOptions { SeleneBinaryPath = " " }, runner));
        Assert.Throws<ArgumentException>(() => new LuauChecker(new LuauToolOptions { DefaultTimeout = TimeSpan.Zero }, runner));
        Assert.Throws<ArgumentException>(() => new LuauChecker(new LuauToolOptions { MaxOutputCharacters = 0 }, runner));
    }

    [Fact]
    public void Adapter_manifest_declares_script_check_only()
    {
        EngineManifest manifest = LuauAdapterManifest.Create();

        Assert.Equal("roblox", manifest.EngineId);
        Assert.Equal(new[] { EngineCapability.ScriptCheck }, manifest.Capabilities);
        Assert.False(manifest.NeedsGuiHost);
        Assert.Equal(3, manifest.Version.Split('.').Length);
    }
}
