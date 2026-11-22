using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

public sealed class PoolPipePlanTests
{
    private const string _helper = "/opt/bazaar/bazaar-pool-spawn";

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("s1")]
    [InlineData("A")]
    public void A_session_id_of_letters_and_digits_names_the_fifos(string id)
    {
        var (commands, replies) = PoolPipePlan.FifoPaths("/run/bazaar-pool/pipes", id);
        Assert.Equal("/run/bazaar-pool/pipes/" + id + "-fd3", commands);
        Assert.Equal("/run/bazaar-pool/pipes/" + id + "-fd4", replies);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a b")]
    [InlineData("a;b")]
    [InlineData("a-b")]
    [InlineData("a\0b")]
    [InlineData("é")]
    public void A_session_id_that_is_not_plain_is_refused(string id)
    {
        Assert.False(PoolPipePlan.IsValidSessionId(id));
        Assert.Throws<ArgumentException>(() => PoolPipePlan.FifoPaths("/run/p", id));
    }

    [Fact]
    public void An_overlong_session_id_is_refused()
    {
        Assert.True(PoolPipePlan.IsValidSessionId(new string('a', PoolPipePlan.MaxSessionIdLength)));
        Assert.False(PoolPipePlan.IsValidSessionId(new string('a', PoolPipePlan.MaxSessionIdLength + 1)));
    }

    [Fact]
    public void A_bad_pipe_directory_is_refused_when_naming_fifos()
    {
        Assert.Throws<ArgumentException>(() => PoolPipePlan.FifoPaths("relative/dir", "s1"));
        Assert.Throws<ArgumentException>(() => PoolPipePlan.FifoPaths("/run/../etc", "s1"));
    }

    [Theory]
    [InlineData("/run/p", true)]
    [InlineData("/a-b/c_d/e.f+g", true)]
    [InlineData("run/p", false)]
    [InlineData("/run/p/", false)]
    [InlineData("/run//p", false)]
    [InlineData("/run/./p", false)]
    [InlineData("/run/../p", false)]
    [InlineData("/run/p q", false)]
    [InlineData("/run/p;q", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Safe_absolute_paths(string? path, bool expected) => Assert.Equal(expected, PoolPipePlan.IsSafeAbsolutePath(path));

    [Theory]
    [InlineData("/run/bazaar-pool/pipes", "/run", true)]
    [InlineData("/run/bazaar-pool/pipes", "/run/bazaar-pool/pipes", true)]
    [InlineData("/run/bazaar-pool/pipes", "/run/bazaar-pool/pipes/x-fd3", true)]
    [InlineData("/run/bazaar-pool/pipes", "/", true)]
    [InlineData("/run/bazaar-pool/pipes", "/run/bazaar-pool/pipes2", false)]
    [InlineData("/run/bazaar-pool/pipes", "/run/user/browser", false)]
    [InlineData("/run/bazaar-pool/pipes", "/usr", false)]
    public void Overlap_means_equal_above_or_below(string dir, string other, bool expected) => Assert.Equal(expected, PoolPipePlan.Overlaps(dir, other));

    [Fact]
    public void A_launch_that_names_the_pipe_directory_is_a_conflict()
    {
        Assert.Null(PoolPipePlan.FindConflict("/run/bazaar-pool/pipes", ["/usr/bin/setpriv", "--bind", "/srv/pool/slot-1/profile", "/home/browser", "--setenv", "PATH", "/usr/bin:/bin"]));
        Assert.Equal("/run/bazaar-pool", PoolPipePlan.FindConflict("/run/bazaar-pool/pipes", ["/usr/bin/bwrap", "--bind", "/run/bazaar-pool", "/x"]));
        Assert.Equal("/run", PoolPipePlan.FindConflict("/run/bazaar-pool/pipes", ["/usr/bin/bwrap", "--ro-bind", "/run", "/run"]));
    }

    [Fact]
    public void The_helper_goes_first_and_the_launch_argv_is_otherwise_unchanged()
    {
        string[] launch = ["/usr/bin/systemd-run", "--scope", "--", "/usr/bin/setpriv", "--reuid", "10001", "--", "/usr/bin/bwrap", "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 10.0.0.2", "--remote-debugging-pipe", "--", "http://example.invalid/"];
        var argv = PoolPipePlan.Compose(_helper, "/p/s1-fd3", "/p/s1-fd4", launch, _ => null);

        Assert.Equal([_helper, "--fd3", "/p/s1-fd3", "--fd4", "/p/s1-fd4", "--"], argv.Take(6));
        Assert.Equal(launch, argv.Skip(6));
    }

    [Fact]
    public void A_bare_program_name_is_replaced_by_its_resolved_absolute_path_and_nothing_else()
    {
        string[] launch = ["systemd-run", "--scope", "--", "/usr/bin/chromium"];
        var argv = PoolPipePlan.Compose(_helper, "/p/a", "/p/b", launch, name => name == "systemd-run" ? "/usr/bin/systemd-run" : null);

        Assert.Equal(["/usr/bin/systemd-run", "--scope", "--", "/usr/bin/chromium"], argv.Skip(6));
    }

    [Fact]
    public void A_program_that_cannot_be_found_fails_without_naming_other_arguments()
    {
        var ex = Assert.Throws<PoolHostException>(() => PoolPipePlan.Compose(_helper, "/p/a", "/p/b", ["nosuchprog", "--proxy-server=http://10.9.9.9:3128"], _ => null));
        Assert.DoesNotContain("10.9.9.9", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolver_that_returns_a_relative_path_is_refused()
    {
        Assert.Throws<PoolHostException>(() => PoolPipePlan.Compose(_helper, "/p/a", "/p/b", ["prog"], _ => "bin/prog"));
    }

    [Fact]
    public void Path_search_uses_only_absolute_directories_in_order()
    {
        var existing = new HashSet<string>(StringComparer.Ordinal) { "/b/tool", "/c/tool" };
        Assert.Equal("/b/tool", PoolPipePlan.ResolveOnPath("tool", "rel:/a::/b/:/c", existing.Contains));
        Assert.Null(PoolPipePlan.ResolveOnPath("tool", "rel:.", existing.Contains));
        Assert.Null(PoolPipePlan.ResolveOnPath("tool", null, existing.Contains));
        Assert.Null(PoolPipePlan.ResolveOnPath("a/tool", "/b", existing.Contains));
    }
}

public sealed class PoolHostPipeOptionsTests
{
    [Fact]
    public void Defaults_have_no_helper_and_are_valid()
    {
        var options = new LinuxPoolHostOptions();
        Assert.Null(options.PoolSpawnHelperPath);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void An_absolute_helper_and_directory_are_valid()
    {
        Assert.Empty(new LinuxPoolHostOptions { PoolSpawnHelperPath = "/opt/bazaar/bazaar-pool-spawn" }.Validate());
    }

    [Theory]
    [InlineData("bazaar-pool-spawn")]
    [InlineData("./bazaar-pool-spawn")]
    [InlineData("/opt/../bin/x")]
    [InlineData("/opt/bazaar pool/x")]
    [InlineData("")]
    public void A_helper_that_is_not_a_safe_absolute_path_is_refused(string path)
    {
        var errors = new LinuxPoolHostOptions { PoolSpawnHelperPath = path }.Validate();
        Assert.Contains(errors, e => e.Contains("PoolSpawnHelperPath", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pipes")]
    [InlineData("/run/../pipes")]
    [InlineData("/run/pipes/")]
    public void A_pipe_directory_that_is_not_a_safe_absolute_path_is_refused_when_the_helper_is_set(string dir)
    {
        var errors = new LinuxPoolHostOptions { PoolSpawnHelperPath = "/opt/x/spawn", PipeDirectory = dir }.Validate();
        Assert.Contains(errors, e => e.Contains("PipeDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pipe_directory_is_not_checked_while_pipes_are_off()
    {
        Assert.Empty(new LinuxPoolHostOptions { PipeDirectory = "nonsense" }.Validate());
    }

    [Fact]
    public void The_helper_may_not_sit_inside_the_pipe_directory()
    {
        var errors = new LinuxPoolHostOptions { PoolSpawnHelperPath = "/run/bazaar-pool/pipes/spawn" }.Validate();
        Assert.Contains(errors, e => e.Contains("inside PipeDirectory", StringComparison.Ordinal));
    }
}

public sealed class LinuxPoolVmHostPipeTests : IDisposable
{
    private const string _helper = "/opt/bazaar/bazaar-pool-spawn";
    private const string _dir = "/run/bazaar-pool/pipes";
    private const string _document = "table inet bazaar_pool\n";

    private static readonly PoolFirewallRule _rule = new(10001, "10.0.0.2", 3128);

    private readonly FakeFifoFactory _fifos = new();

    public void Dispose() => _fifos.Dispose();

    private static string Listing(PoolFirewallRule rule) =>
        "table inet bazaar_pool {\n\tchain output {\n\t\ttype filter hook output priority filter; policy accept;\n"
        + $"\t\tmeta skuid {rule.Uid} ip daddr {rule.ProxyHost} tcp dport {rule.ProxyPort} accept\n"
        + "\t\tmeta skuid >= 10000 meta skuid <= 59999 drop\n\t}\n"
        + "\tchain input {\n\t\ttype filter hook input priority filter; policy accept;\n\t\tiifname \"bzv*\" drop\n\t}\n"
        + "\tchain forward {\n\t\ttype filter hook forward priority filter; policy accept;\n\t\tiifname \"bzv*\" drop\n\t}\n}\n";

    private static FakeProcessRunner Runner(bool firewallOk = true) =>
        new(r => r.Argv.Count > 1 && r.Argv[1] == "list" ? (firewallOk ? FakeProcessRunner.Ok(Listing(_rule)) : FakeProcessRunner.Ok("table inet bazaar_pool\n")) : FakeProcessRunner.Ok());

    private LinuxPoolVmHost Host(FakeProcessRunner runner, string? helper = _helper) =>
        new(runner, _ => _document, new LinuxPoolHostOptions { PoolSpawnHelperPath = helper, PipeDirectory = _dir, ProbeEnabled = false }, requireLinux: false, fifos: _fifos);

    private static PoolLaunchSpec Spec(string session = "s1", params string[] launch) =>
        new(session, "agent-a", 1, 10001, [], launch.Length == 0 ? ["/usr/bin/setpriv", "--reuid", "10001", "--", "/usr/bin/chromium", "--remote-debugging-pipe"] : launch, [], _rule);

    [Fact]
    public async Task With_the_helper_set_the_started_argv_has_the_helper_first_and_the_fifo_paths()
    {
        var runner = Runner();
        var host = Host(runner);

        await host.StartAsync(Spec("s1"), TestContext.Current.CancellationToken);

        var started = Assert.Single(runner.Started);
        Assert.Equal(
            [_helper, "--fd3", _dir + "/s1-fd3", "--fd4", _dir + "/s1-fd4", "--", "/usr/bin/setpriv", "--reuid", "10001", "--", "/usr/bin/chromium", "--remote-debugging-pipe"],
            started.Request.Argv);
        Assert.Equal([_dir + "/s1-fd3", _dir + "/s1-fd4"], _fifos.Created);
        Assert.Equal(_dir, _fifos.Directory);
    }

    [Fact]
    public async Task Without_the_helper_the_argv_is_unchanged_and_no_fifo_is_made()
    {
        var runner = Runner();
        var host = Host(runner, helper: null);
        var spec = Spec("s1");

        await host.StartAsync(spec, TestContext.Current.CancellationToken);

        Assert.Equal(spec.Launch, Assert.Single(runner.Started).Request.Argv);
        Assert.Empty(_fifos.Created);
        Assert.False(host.TryGetPipe("s1", out _));
    }

    [Fact]
    public async Task The_firewall_check_runs_first_and_a_failure_makes_no_fifo()
    {
        var runner = Runner(firewallOk: false);
        var host = Host(runner);

        await Assert.ThrowsAsync<PoolFirewallException>(() => host.StartAsync(Spec(), TestContext.Current.CancellationToken));

        Assert.Empty(runner.Started);
        Assert.Empty(_fifos.Created);
    }

    [Fact]
    public async Task The_pipe_is_available_while_running_and_gone_after_stop_and_the_fifos_are_removed()
    {
        var runner = Runner();
        var host = Host(runner);
        var process = await host.StartAsync(Spec("s1"), TestContext.Current.CancellationToken);

        Assert.True(host.TryGetPipe("s1", out var pipe));
        Assert.NotNull(pipe);
        Assert.True(pipe.ToBrowser.CanWrite);
        Assert.True(pipe.FromBrowser.CanRead);
        Assert.False(host.TryGetPipe("other", out _));

        await host.StopAsync(process, TestContext.Current.CancellationToken);

        Assert.False(host.TryGetPipe("s1", out _));
        Assert.Contains(_dir + "/s1-fd3", _fifos.Deleted);
        Assert.Contains(_dir + "/s1-fd4", _fifos.Deleted);
        Assert.Throws<ObjectDisposedException>(() => pipe.ToBrowser.WriteByte(1));
    }

    [Fact]
    public async Task A_browser_that_exits_by_itself_releases_the_pipe_and_removes_the_fifos()
    {
        var runner = Runner();
        var host = Host(runner);
        await host.StartAsync(Spec("s1"), TestContext.Current.CancellationToken);
        var created = _fifos.Created.Count;

        runner.Started[0].Exit(0);
        var deadline = DateTime.UtcNow.AddSeconds(10);

        // The exit continuation removes the pipe from the table first and deletes the FIFOs next, so wait for both.
        while ((host.TryGetPipe("s1", out _) || _fifos.Deleted.Count < 2) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(host.TryGetPipe("s1", out _));
        Assert.Equal(2, created);
        Assert.Contains(_dir + "/s1-fd3", _fifos.Deleted);
    }

    [Fact]
    public async Task A_failed_spawn_removes_the_fifos_and_leaves_no_pipe()
    {
        var runner = Runner();
        runner.StartFailure = new InvalidOperationException("no such file");
        var host = Host(runner);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(Spec("s1"), TestContext.Current.CancellationToken));

        Assert.Contains(_dir + "/s1-fd3", _fifos.Deleted);
        Assert.Contains(_dir + "/s1-fd4", _fifos.Deleted);
        Assert.False(host.TryGetPipe("s1", out _));
    }

    [Fact]
    public async Task A_session_id_that_cannot_name_a_file_is_refused_before_anything_is_made()
    {
        var runner = Runner();
        var host = Host(runner);

        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(Spec("../etc/x"), TestContext.Current.CancellationToken));

        Assert.Empty(runner.Started);
        Assert.Empty(_fifos.Created);
    }

    [Fact]
    public async Task A_launch_that_binds_the_pipe_directory_is_refused()
    {
        var runner = Runner();
        var host = Host(runner);

        await Assert.ThrowsAsync<PoolHostException>(() => host.StartAsync(Spec("s1", "/usr/bin/bwrap", "--bind", "/run/bazaar-pool", "/x", "/usr/bin/chromium"), TestContext.Current.CancellationToken));

        Assert.Empty(runner.Started);
        Assert.Empty(_fifos.Created);
    }

    [Fact]
    public async Task A_second_start_with_the_same_session_id_is_refused_while_the_first_runs()
    {
        var runner = Runner();
        var host = Host(runner);
        await host.StartAsync(Spec("s1"), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PoolHostException>(() => host.StartAsync(Spec("s1"), TestContext.Current.CancellationToken));

        Assert.Single(runner.Started);
    }

    [Fact]
    public async Task A_bare_program_that_is_not_on_the_path_is_refused_and_nothing_is_made()
    {
        var runner = Runner();
        var host = Host(runner);

        await Assert.ThrowsAsync<PoolHostException>(() => host.StartAsync(Spec("s1", "bazaar-no-such-program-for-test", "--x"), TestContext.Current.CancellationToken));

        Assert.Empty(runner.Started);
        Assert.Empty(_fifos.Created);
    }
}
