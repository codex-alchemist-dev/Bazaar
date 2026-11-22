namespace Bazaar.Server.VmEgress.Enforcers.Tests;

public class NftRootCommandRunnerTests
{
    [Theory]
    [InlineData("-f", "-")]
    [InlineData("list", "table", "bridge", "bazaar_egress_a1")]
    [InlineData("list", "table", "bridge", "bazaar_egress_h0123456789abcdef0123456789abcde")]
    public async Task Allowed_argument_lists_reach_the_host_runner_as_nft(params string[] args)
    {
        var host = new FakeHostRunner();

        await new NftRootCommandRunner(host).RunAsync(new RootCommand(args, "stdin text"));

        var command = Assert.Single(host.Commands);
        Assert.Equal(HostTool.Nft, command.Tool);
        Assert.Equal(args, command.Arguments);
        Assert.Equal("stdin text", command.StandardInput);
    }

    [Theory]
    [InlineData("flush", "ruleset")]
    [InlineData("-f", "/etc/nftables.conf")]
    [InlineData("-f", "-", "extra")]
    [InlineData("-f")]
    [InlineData("list", "ruleset")]
    [InlineData("list", "table", "inet", "bazaar_egress_a1")]
    [InlineData("list", "table", "bridge", "filter")]
    [InlineData("list", "table", "bridge", "bazaar_egress_a1; flush ruleset")]
    [InlineData("list", "table", "bridge", "bazaar_egress_a1\n")]
    [InlineData("list", "table", "bridge", "bazaar_egress_")]
    [InlineData("--json", "list", "ruleset")]
    public async Task Any_other_argument_list_is_refused_before_a_process_runs(params string[] args)
    {
        var host = new FakeHostRunner();

        await Assert.ThrowsAsync<ArgumentException>(() => new NftRootCommandRunner(host).RunAsync(new RootCommand(args, string.Empty)));

        Assert.Empty(host.Commands);
    }
}

public class ProcessHostCommandRunnerTests
{
    // The test resolver maps the allowed tool name to a harmless binary, which proves the plumbing without nft or PowerShell.
    private static ProcessHostCommandRunner Runner(string binary, TimeSpan? timeout = null) =>
        new(timeout ?? TimeSpan.FromSeconds(20), _ => binary);

    private static void RequireLinux() => Assert.SkipUnless(OperatingSystem.IsLinux(), "Needs Linux binaries.");

    [Fact]
    public async Task A_tool_outside_the_allow_list_is_rejected()
    {
        var runner = new ProcessHostCommandRunner();

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new HostCommand((HostTool)99, [], string.Empty)));
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(new HostCommand((HostTool)(-1), [], string.Empty)));
    }

    [Fact]
    public async Task A_tool_for_another_platform_is_refused_without_starting_anything()
    {
        var runner = new ProcessHostCommandRunner();
        var foreign = OperatingSystem.IsWindows() ? HostTool.Nft : HostTool.PowerShell;

        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => runner.RunAsync(new HostCommand(foreign, [], string.Empty)));
    }

    [Fact]
    public void A_non_positive_timeout_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessHostCommandRunner(TimeSpan.Zero));
    }

    [Fact]
    public async Task Standard_input_reaches_the_tool_and_output_comes_back()
    {
        RequireLinux();

        var result = await Runner("/bin/cat").RunAsync(new HostCommand(HostTool.Nft, [], "ruleset text é\n"));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ruleset text é\n", result.StandardOutput);
    }

    [Fact]
    public async Task Arguments_are_passed_as_argv_not_through_a_shell()
    {
        RequireLinux();

        var result = await Runner("/bin/sh").RunAsync(new HostCommand(HostTool.Nft, ["-c", "printf '%s' \"$1\"", "sh", "a b; echo injected $(id)"], string.Empty));

        Assert.Equal("a b; echo injected $(id)", result.StandardOutput);
    }

    [Fact]
    public async Task The_environment_additions_reach_the_tool()
    {
        RequireLinux();

        var result = await Runner("/bin/sh").RunAsync(new HostCommand(HostTool.Nft, ["-c", "printf '%s' \"$BAZAAR_TEST_VALUE\""], string.Empty, new Dictionary<string, string> { ["BAZAAR_TEST_VALUE"] = "v1" }));

        Assert.Equal("v1", result.StandardOutput);
    }

    [Fact]
    public async Task A_failing_tool_reports_its_exit_code_and_stderr()
    {
        RequireLinux();

        var result = await Runner("/bin/sh").RunAsync(new HostCommand(HostTool.Nft, ["-c", "echo oops >&2; exit 3"], string.Empty));

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("oops", result.StandardError);
    }

    [Fact]
    public async Task Output_over_the_cap_throws_instead_of_growing_without_bound()
    {
        RequireLinux();

        await Assert.ThrowsAsync<EgressEnforcementException>(() => Runner("/usr/bin/yes").RunAsync(new HostCommand(HostTool.Nft, [], string.Empty)));
    }

    [Fact]
    public async Task A_run_past_the_timeout_is_killed_and_reported()
    {
        RequireLinux();
        var started = DateTime.UtcNow;

        var e = await Assert.ThrowsAsync<EgressEnforcementException>(() => Runner("/bin/sleep", TimeSpan.FromMilliseconds(300)).RunAsync(new HostCommand(HostTool.Nft, ["30"], string.Empty)));

        Assert.Contains("timed out", e.Message);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancellation_kills_the_whole_process_tree()
    {
        RequireLinux();
        var marker = Path.Combine(Path.GetTempPath(), "bazaar-egress-child-" + Guid.NewGuid().ToString("N"));
        using var cts = new CancellationTokenSource();

        // The shell starts a background child that holds its output pipes; killing only the shell would leave it running.
        var run = Runner("/bin/sh").RunAsync(new HostCommand(HostTool.Nft, ["-c", $"(sleep 30; touch {marker}) & sleep 30"], string.Empty), cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_missing_executable_is_reported_as_an_enforcement_failure()
    {
        RequireLinux();

        await Assert.ThrowsAsync<EgressEnforcementException>(() => Runner("/nonexistent/tool").RunAsync(new HostCommand(HostTool.Nft, [], string.Empty)));
    }
}
