using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

public sealed class LinuxPoolVmHostTests
{
    private const string _document = "table inet bazaar_pool\n";

    private static readonly PoolFirewallRule _rule = new(10001, "10.0.0.2", 3128);

    private static string Listing(params PoolFirewallRule[] rules)
    {
        var accepts = string.Concat(rules.Select(r => $"\t\tmeta skuid {r.Uid} ip daddr {r.ProxyHost} tcp dport {r.ProxyPort} accept\n"));
        return "table inet bazaar_pool {\n\tchain output {\n\t\ttype filter hook output priority filter; policy accept;\n"
            + accepts
            + "\t\tmeta skuid >= 10000 meta skuid <= 59999 drop\n\t}\n"
            + "\tchain input {\n\t\ttype filter hook input priority filter; policy accept;\n\t\tiifname \"bzv*\" drop\n\t}\n"
            + "\tchain forward {\n\t\ttype filter hook forward priority filter; policy accept;\n\t\tiifname \"bzv*\" drop\n\t}\n}\n";
    }

    private static bool IsList(ProcessRequest r) => r.Argv.Count > 1 && r.Argv[1] == "list";

    private static LinuxPoolVmHost Host(FakeProcessRunner runner, Func<IReadOnlyList<PoolFirewallRule>, string>? render = null) =>
        new(runner, render ?? (_ => _document), new LinuxPoolHostOptions { ProbeEnabled = false }, requireLinux: false);

    private static PoolLaunchSpec Spec(PoolFirewallRule? rule = null) =>
        new("s1", "agent-a", 1, 10001, [], ["/usr/bin/chromium", "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 10.0.0.2"], [], rule ?? _rule);

    [Fact]
    public void Public_constructor_refuses_a_non_Linux_os()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux(), "Only meaningful off Linux.");
        Assert.Throws<PlatformNotSupportedException>(() => new LinuxPoolVmHost(_ => _document));
    }

    [Fact]
    public void Invalid_options_are_refused()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        Assert.Throws<ArgumentException>(() => new LinuxPoolVmHost(runner, _ => _document, new LinuxPoolHostOptions { TableName = "bad name; rm" }, requireLinux: false));
    }

    [Fact]
    public async Task Run_passes_the_argv_unchanged_and_accepts_exit_zero()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        var argv = new[] { "/bin/mkdir", "-p", "/srv/a b; echo $HOME" };
        await Host(runner).RunAsync(new PoolCommand(argv), TestContext.Current.CancellationToken);

        var request = Assert.Single(runner.Runs);
        Assert.Equal(argv, request.Argv);
        Assert.Null(request.StdIn);
        Assert.True(request.Timeout > TimeSpan.Zero);
    }

    [Fact]
    public async Task Run_throws_on_a_code_outside_the_allowed_list_and_names_only_the_program()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail(3, "boom"));
        var ex = await Assert.ThrowsAsync<PoolCommandFailedException>(() =>
            Host(runner).RunAsync(new PoolCommand(["/bin/tool", "--secret-looking-arg"]), TestContext.Current.CancellationToken));

        Assert.Equal(3, ex.ExitCode);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--secret-looking-arg", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_honours_accepted_exit_codes()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail(2));
        await Host(runner).RunAsync(new PoolCommand(["/bin/rm", "x"], [0, 2]), TestContext.Current.CancellationToken);

        var strict = new FakeProcessRunner(_ => FakeProcessRunner.Fail(0 + 1));
        await Assert.ThrowsAsync<PoolCommandFailedException>(() =>
            Host(strict).RunAsync(new PoolCommand(["/bin/rm", "x"], [0, 2]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAll_runs_every_command_after_a_failure_and_reports_each_failure()
    {
        var calls = 0;
        var runner = new FakeProcessRunner(r =>
        {
            calls++;
            return r.Argv[0] is "/bin/a" or "/bin/c" ? FakeProcessRunner.Fail(1, "bad " + r.Argv[0]) : FakeProcessRunner.Ok();
        });

        var ex = await Assert.ThrowsAsync<AggregateException>(() => Host(runner).RunAllAsync(
            [new PoolCommand(["/bin/a"]), new PoolCommand(["/bin/b"]), new PoolCommand(["/bin/c"])],
            TestContext.Current.CancellationToken));

        Assert.Equal(3, calls);
        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, e => Assert.IsType<PoolCommandFailedException>(e));
    }

    [Fact]
    public async Task RunAll_succeeds_quietly_when_nothing_fails()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        await Host(runner).RunAllAsync([new PoolCommand(["/bin/a"]), new PoolCommand(["/bin/b"])], TestContext.Current.CancellationToken);
        Assert.Equal(2, runner.Runs.Count);
    }

    [Fact]
    public async Task Apply_feeds_the_rendered_text_on_stdin_then_verifies()
    {
        var runner = new FakeProcessRunner(r => IsList(r) ? FakeProcessRunner.Ok(Listing(_rule)) : FakeProcessRunner.Ok());
        IReadOnlyList<PoolFirewallRule>? rendered = null;
        var host = Host(runner, rules =>
        {
            rendered = rules;
            return _document;
        });

        await host.ApplyFirewallAsync([_rule], TestContext.Current.CancellationToken);

        Assert.Equal([_rule], rendered);
        Assert.Equal(2, runner.Runs.Count);
        Assert.Equal(["nft", "-f", "-"], runner.Runs[0].Argv);
        Assert.Equal(_document, runner.Runs[0].StdIn);
        Assert.Equal(["nft", "list", "table", "inet", "bazaar_pool"], runner.Runs[1].Argv);
    }

    [Fact]
    public async Task Apply_fails_when_nft_rejects_the_ruleset_and_does_not_verify()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail(1, "syntax error"));
        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => Host(runner).ApplyFirewallAsync([_rule], TestContext.Current.CancellationToken));

        Assert.Contains("syntax error", ex.Message, StringComparison.Ordinal);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task Apply_fails_when_the_table_is_missing_after_apply()
    {
        var runner = new FakeProcessRunner(r => IsList(r) ? FakeProcessRunner.Fail(1, "No such file or directory") : FakeProcessRunner.Ok());
        await Assert.ThrowsAsync<PoolFirewallException>(() => Host(runner).ApplyFirewallAsync([_rule], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apply_fails_when_the_drop_rule_is_missing_after_apply()
    {
        const string noDrop = "table inet bazaar_pool {\n\tchain output {\n\t\ttype filter hook output priority filter; policy accept;\n\t}\n}\n";
        var runner = new FakeProcessRunner(r => IsList(r) ? FakeProcessRunner.Ok(noDrop) : FakeProcessRunner.Ok());
        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => Host(runner).ApplyFirewallAsync([], TestContext.Current.CancellationToken));
        Assert.Contains("drop", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_fails_when_an_expected_accept_rule_is_missing()
    {
        var runner = new FakeProcessRunner(r => IsList(r) ? FakeProcessRunner.Ok(Listing()) : FakeProcessRunner.Ok());
        var ex = await Assert.ThrowsAsync<PoolFirewallException>(() => Host(runner).ApplyFirewallAsync([_rule], TestContext.Current.CancellationToken));
        Assert.Contains("10001", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_turns_a_renderer_refusal_into_a_firewall_failure_without_running_nft()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        var host = Host(runner, _ => throw new ArgumentException("A proxy host must be an IPv4 address literal."));

        await Assert.ThrowsAsync<PoolFirewallException>(() => host.ApplyFirewallAsync([_rule], TestContext.Current.CancellationToken));
        Assert.Empty(runner.Runs);
    }

    [Fact]
    public async Task Apply_turns_a_timeout_or_a_missing_nft_into_a_firewall_failure()
    {
        var timesOut = new FakeThrowingRunner(new TimeoutException("slow"));
        await Assert.ThrowsAsync<PoolFirewallException>(() =>
            new LinuxPoolVmHost(timesOut, _ => _document, null, requireLinux: false).ApplyFirewallAsync([_rule], TestContext.Current.CancellationToken));

        var missing = new FakeThrowingRunner(new InvalidOperationException("Could not start nft"));
        await Assert.ThrowsAsync<PoolFirewallException>(() =>
            new LinuxPoolVmHost(missing, _ => _document, null, requireLinux: false).ApplyFirewallAsync([_rule], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Apply_does_not_swallow_cancellation()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Host(runner).ApplyFirewallAsync([_rule], cts.Token));
    }

    [Fact]
    public async Task Health_check_passes_with_table_and_drop_and_fails_without()
    {
        var good = new FakeProcessRunner(_ => FakeProcessRunner.Ok(Listing()));
        await Host(good).CheckFirewallAsync(TestContext.Current.CancellationToken);

        var gone = new FakeProcessRunner(_ => FakeProcessRunner.Fail(1, "no such table"));
        await Assert.ThrowsAsync<PoolFirewallException>(() => Host(gone).CheckFirewallAsync(TestContext.Current.CancellationToken));

        var flushed = new FakeProcessRunner(_ => FakeProcessRunner.Ok("table inet bazaar_pool {\n}\n"));
        await Assert.ThrowsAsync<PoolFirewallException>(() => Host(flushed).CheckFirewallAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Start_refuses_to_launch_when_the_firewall_is_gone()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Fail(1, "no such table"));
        await Assert.ThrowsAsync<PoolFirewallException>(() => Host(runner).StartAsync(Spec(), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Start_refuses_to_launch_without_this_sessions_accept_rule()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok(Listing(new PoolFirewallRule(10002, "10.0.0.2", 3128))));
        await Assert.ThrowsAsync<PoolFirewallException>(() => Host(runner).StartAsync(Spec(), TestContext.Current.CancellationToken));
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Start_launches_the_argv_when_the_firewall_holds_and_stop_kills_it()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok(Listing(_rule)));
        var host = Host(runner);
        var spec = Spec();

        var process = await host.StartAsync(spec, TestContext.Current.CancellationToken);

        var started = Assert.Single(runner.Started);
        Assert.Equal(spec.Launch, started.Request.Argv);
        Assert.Equal("s1", process.SessionId);
        Assert.Equal(started.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), process.GuestProcessId);

        await host.StopAsync(process, TestContext.Current.CancellationToken);
        Assert.Equal(1, started.Kills);

        // A second stop is a no-op, not a failure.
        await host.StopAsync(process, TestContext.Current.CancellationToken);
        Assert.Equal(1, started.Kills);
    }

    [Fact]
    public async Task Exit_of_a_started_browser_is_observable()
    {
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok(Listing(_rule)));
        var host = Host(runner);
        var process = await host.StartAsync(Spec(), TestContext.Current.CancellationToken);

        var wait = host.WaitForExitAsync(process, TestContext.Current.CancellationToken);
        Assert.False(wait.IsCompleted);
        runner.Started[0].Exit(7);
        Assert.Equal(7, await wait);
    }

    [Fact]
    public async Task Wait_for_an_unknown_process_throws()
    {
        var host = Host(new FakeProcessRunner(_ => FakeProcessRunner.Ok()));
        await Assert.ThrowsAsync<PoolHostException>(() => host.WaitForExitAsync(new PoolProcess("s", "99999"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Start_with_an_empty_launch_is_refused()
    {
        var host = Host(new FakeProcessRunner(_ => FakeProcessRunner.Ok(Listing(_rule))));
        var spec = Spec() with { Launch = [] };
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(spec, TestContext.Current.CancellationToken));
    }

    private sealed class FakeThrowingRunner : IProcessRunner
    {
        private readonly Exception _exception;

        public FakeThrowingRunner(Exception exception) => _exception = exception;

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct) => Task.FromException<ProcessResult>(_exception);

        public IRunningProcess Start(ProcessRequest request) => throw _exception;
    }
}
