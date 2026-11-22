using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>
/// Real processes: the real <c>bazaar-pool-spawn</c> tool, real FIFOs, real <c>setpriv</c> and <c>bwrap</c> (and <c>cage</c> and
/// Chromium when present). Skipped unless BAZAAR_TEST_REAL_NETNS=1, the OS is Linux and the process is root, the same gate as the
/// other real tests. Each test skips with its own reason when a tool it needs is missing.
/// </summary>
[Collection("linuxhost-real")]
[SupportedOSPlatform("linux")]
public sealed class RealPipeTests : IDisposable
{
    private const string _table = "bazaar_pool_pipetest";
    private const string _chrome = "/opt/pw-browsers/chromium-1243/chrome-linux64/chrome";
    private const int _slot = 0;

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    private readonly string _unique = Guid.NewGuid().ToString("N")[..12];
    private readonly string _temp;
    private readonly string _pipeDir;
    private readonly BrowserPoolOptions _pool;
    private readonly List<Process> _processes = [];
    private bool _touchedTable;

    public RealPipeTests()
    {
        _temp = Path.Combine("/tmp", "bazaar-pipe-test-" + _unique);
        _pipeDir = "/run/bazaar-pool-test-" + _unique + "/pipes";
        _pool = new BrowserPoolOptions
        {
            SlotRoot = _temp + "/pool",
            ChromiumPath = _chrome,
            UseSystemdRun = false,
            MemoryMaxMb = 0,
            MaxSessions = 4,
            MaxSessionsPerAgent = 2,
            ExtraReadOnlyBinds = ["/opt/pw-browsers"],
            NetworkIsolation = NetworkIsolation.SharedWithUidRule,
        };
    }

    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "bazaar-pool-spawn");

    public void Dispose()
    {
        foreach (var p in _processes)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            p.Dispose();
        }

        if (SkipReason() is not null)
        {
            return;
        }

        if (_touchedTable)
        {
            Run(["/usr/sbin/nft", "delete", "table", "inet", _table]);
        }

        foreach (var c in PoolCommandLine.CleanupCommands(PoolCommandLine.SlotUid(_pool, _slot), PoolCommandLine.SlotDirectory(_pool, _slot)))
        {
            Run(c.Argv);
        }

        Run(["rm", "-rf", "--one-file-system", _temp]);
        Run(["rm", "-rf", "--one-file-system", "/run/bazaar-pool-test-" + _unique]);
    }

    private static string? SkipReason()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Not Linux.";
        }

        if (Environment.GetEnvironmentVariable("BAZAAR_TEST_REAL_NETNS") != "1")
        {
            return "Set BAZAAR_TEST_REAL_NETNS=1 to run the tests that use real Linux tools.";
        }

        if (!File.ReadAllText("/proc/self/status").Contains("Uid:\t0\t", StringComparison.Ordinal))
        {
            return "Not running as root.";
        }

        foreach (var tool in new[] { "/usr/sbin/nft", "/usr/bin/stat", "/usr/bin/dd", "/usr/bin/readlink", HelperPath })
        {
            if (!File.Exists(tool))
            {
                return tool + " is not installed.";
            }
        }

        return PoolPipePlan.IsSafeAbsolutePath(HelperPath) ? null : "The test output path has characters the host option refuses: " + HelperPath;
    }

    private static string Render(IReadOnlyList<PoolFirewallRule> rules)
    {
        var accepts = string.Concat(rules.Select(r => $"    meta skuid {r.Uid} ip daddr {r.ProxyHost} tcp dport {r.ProxyPort} accept\n"));
        return $"table inet {_table}\ndelete table inet {_table}\ntable inet {_table} {{\n  chain output {{\n    type filter hook output priority 0; policy accept;\n"
            + accepts
            + "    meta skuid >= 58000 meta skuid <= 58010 drop\n  }\n"
            + "  chain input {\n    type filter hook input priority 0; policy accept;\n    iifname \"bzv*\" drop\n  }\n"
            + "  chain forward {\n    type filter hook forward priority 0; policy accept;\n    iifname \"bzv*\" drop\n  }\n}\n";
    }

    private LinuxPoolVmHost PipeHost() =>
        new(Render, new LinuxPoolHostOptions { TableName = _table, NftPath = "/usr/sbin/nft", FirewallTimeout = _bound, PoolSpawnHelperPath = HelperPath, PipeDirectory = _pipeDir, ProbeEnabled = false });

    private static (int Exit, string Out, string Err) Run(IReadOnlyList<string> argv, int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in argv.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(true);
        }

        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static bool UserNamespaceDenied(string stderr) =>
        stderr.Contains("Operation not permitted", StringComparison.Ordinal) || stderr.Contains("Permission denied", StringComparison.Ordinal) || stderr.Contains("Creating new namespace failed", StringComparison.Ordinal);

    private void PrepareSlot()
    {
        Directory.CreateDirectory(_temp + "/pool");
        Run(["chmod", "0755", _temp]);
        Run(["chmod", "0711", _temp + "/pool"]);
        foreach (var c in PoolCommandLine.PrepareCommands(PoolCommandLine.SlotUid(_pool, _slot), PoolCommandLine.SlotDirectory(_pool, _slot)))
        {
            var r = Run(c.Argv);
            Assert.True(r.Exit == 0, "prepare failed: " + r.Err);
        }
    }

    /// <summary>Creates real FIFOs, opens the host's ends, and runs <paramref name="launch"/> behind the helper. The set stays open until disposed.</summary>
    private (DevToolsPipeSet Set, Process Process, string Fd3, string Fd4) StartThroughHelper(IReadOnlyList<string> launch)
    {
        var (fd3, fd4) = PoolPipePlan.FifoPaths(_pipeDir, "direct" + _unique);
        var set = DevToolsPipeSet.Open(new LibcFifoFactory(), _pipeDir, fd3, fd4);
        var argv = PoolPipePlan.Compose(HelperPath, fd3, fd4, launch, _ => null);
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in argv.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        var process = Process.Start(psi)!;
        _processes.Add(process);
        process.StandardInput.Close();
        return (set, process, fd3, fd4);
    }

    private static async Task<(int Exit, string Out, string Err)> FinishAsync(Process process)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(_bound);
        return (process.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task The_helper_makes_descriptors_3_and_4_the_two_fifos()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);

        var (set, process, fd3, fd4) = StartThroughHelper(["/usr/bin/stat", "-L", "-c", "%F", "/proc/self/fd/3", "/proc/self/fd/4"]);
        using (set)
        {
            var r = await FinishAsync(process);
            Assert.True(r.Exit == 0, r.Err);
            Assert.Equal("fifo\nfifo", r.Out.Trim().ReplaceLineEndings("\n"));
        }

        // The links name the FIFO paths, in the right order: 3 is the commands FIFO, 4 the replies FIFO.
        var (set2, process2, c3, c4) = StartThroughHelper(["/usr/bin/readlink", "/proc/self/fd/3", "/proc/self/fd/4"]);
        using (set2)
        {
            var r = await FinishAsync(process2);
            Assert.True(r.Exit == 0, r.Err);
            Assert.Equal(c3 + "\n" + c4, r.Out.Trim().ReplaceLineEndings("\n"));
        }

        // Closing the set removes both FIFOs.
        Assert.False(File.Exists(fd3));
        Assert.False(File.Exists(fd4));
        Assert.False(File.Exists(c3));
    }

    [Fact]
    public async Task The_helper_refuses_a_missing_program_with_127_and_bad_arguments_with_64_without_echoing_them()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);

        var (set, process, _, _) = StartThroughHelper(["/usr/bin/bazaar-no-such-tool"]);
        using (set)
        {
            var r = await FinishAsync(process);
            Assert.Equal(127, r.Exit);
            Assert.DoesNotContain("bazaar-no-such-tool", r.Err, StringComparison.Ordinal);
        }

        var bad = Run([HelperPath, "--fd3", "relative", "--fd4", "/x", "--", "/usr/bin/true"]);
        Assert.Equal(64, bad.Exit);
        Assert.DoesNotContain("relative", bad.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Through_the_host_a_message_written_to_the_pipe_comes_back_on_the_other_end_and_the_pipe_then_ends()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;
        var host = PipeHost();
        _touchedTable = true;
        var rule = new PoolFirewallRule(58001, "127.0.0.1", 3128);
        await host.ApplyFirewallAsync([rule], ct);

        // dd copies one read from descriptor 3 to descriptor 4 and exits. It runs as root here, so reopening the FIFOs is allowed.
        var spec = new PoolLaunchSpec("echo" + _unique, "agent-a", 1, rule.Uid, [], ["/usr/bin/dd", "if=/proc/self/fd/3", "of=/proc/self/fd/4", "bs=65536", "count=1"], [], rule);
        var process = await host.StartAsync(spec, ct);
        Assert.True(host.TryGetPipe(spec.SessionId, out var pipe));

        var message = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.getVersion\"}");
        using var writer = new DevToolsMessageWriter(pipe.ToBrowser);
        var reader = new DevToolsMessageReader(pipe.FromBrowser);
        await writer.WriteAsync(message, ct);

        var reply = await reader.ReadAsync(ct).AsTask().WaitAsync(_bound, ct);
        Assert.Equal(message, reply);

        // dd exited: the replies stream reaches end of file, so a reader never hangs on a dead browser.
        Assert.Null(await reader.ReadAsync(ct).AsTask().WaitAsync(_bound, ct));
        await host.StopAsync(process, ct);
        Assert.False(host.TryGetPipe(spec.SessionId, out _));
        Assert.Empty(Directory.GetFileSystemEntries(_pipeDir));
    }

    [Fact]
    public async Task Descriptors_3_and_4_survive_setpriv_and_bwrap_and_the_slot_user_cannot_open_the_fifo_paths()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        PrepareSlot();
        var prefix = PoolCommandLine.SandboxPrefix(_pool, _slot, headed: false);
        var probe = Run([.. prefix, "/usr/bin/id", "-u"]);
        Assert.SkipWhen(probe.Exit != 0 && UserNamespaceDenied(probe.Err), "Unprivileged user namespaces are refused here: " + probe.Err.Trim());

        var (set, process, fd3, _) = StartThroughHelper([.. prefix, "/usr/bin/stat", "-L", "-c", "%F %a", "/proc/self/fd/3", "/proc/self/fd/4"]);
        using (set)
        {
            // While the FIFOs exist, the slot uid cannot reach them from outside the sandbox either: the directory is root-only.
            Assert.True(File.Exists(fd3) || Directory.Exists(_pipeDir));
            Assert.NotEqual(0, Run(["/usr/bin/setpriv", "--reuid", "20000", "--regid", "20000", "--clear-groups", "/usr/bin/stat", fd3]).Exit);
            var r = await FinishAsync(process);
            Assert.True(r.Exit == 0, r.Err);
            // Still the 0600 FIFOs (root-owned on the host, so not the slot's), inside the sandbox, held by the dropped uid.
            Assert.Equal("fifo 600\nfifo 600", r.Out.Trim().ReplaceLineEndings("\n"));
        }

        // The sandbox's own identity is the slot uid, and the FIFO path is not visible to it.
        var who = Run([.. prefix, "/usr/bin/id", "-u"]);
        Assert.Equal("20000", who.Out.Trim());
        Assert.NotEqual(0, Run([.. prefix, "/usr/bin/ls", fd3]).Exit);
        Assert.NotEqual(0, Run([.. prefix, "/usr/bin/ls", "/run/bazaar-pool-test-" + _unique]).Exit);

        // Only 0, 1, 2, 3 and 4 are open (plus the directory handle ls itself has).
        var (set2, process2, _, _) = StartThroughHelper([.. prefix, "/usr/bin/ls", "/proc/self/fd"]);
        using (set2)
        {
            var r = await FinishAsync(process2);
            var open = r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).Where(n => n < 100).Order().ToList();
            Assert.Contains(3, open);
            Assert.Contains(4, open);
            Assert.All(open, n => Assert.True(n <= 5, "unexpected descriptor " + n.ToString(CultureInfo.InvariantCulture)));
        }
    }

    [Fact]
    public async Task A_program_inside_setpriv_and_bwrap_can_read_a_command_and_write_a_reply()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        // /usr/bin/python3 is usually a link through /etc/alternatives, which the sandbox does not have: use the real file under /usr.
        var python = File.Exists("/usr/bin/python3") ? (new FileInfo("/usr/bin/python3").ResolveLinkTarget(true)?.FullName ?? "/usr/bin/python3") : null;
        Assert.SkipWhen(python is null || !python.StartsWith("/usr/", StringComparison.Ordinal), "No python3 under /usr (it stands in for a program that uses descriptors 3 and 4 directly).");
        var ct = TestContext.Current.CancellationToken;
        PrepareSlot();
        var prefix = PoolCommandLine.SandboxPrefix(_pool, _slot, headed: false);
        var probe = Run([.. prefix, "/usr/bin/id", "-u"]);
        Assert.SkipWhen(probe.Exit != 0 && UserNamespaceDenied(probe.Err), "Unprivileged user namespaces are refused here: " + probe.Err.Trim());

        var host = PipeHost();
        _touchedTable = true;
        var rule = new PoolFirewallRule(PoolCommandLine.SlotUid(_pool, _slot), "127.0.0.1", 3128);
        await host.ApplyFirewallAsync([rule], ct);
        var spec = new PoolLaunchSpec("sandbox" + _unique, "agent-a", _slot, rule.Uid, [], [.. prefix, python!, "-I", "-c", "import os; os.write(4, os.read(3, 65536))"], [], rule);
        var process = await host.StartAsync(spec, ct);
        Assert.True(host.TryGetPipe(spec.SessionId, out var pipe));

        using var writer = new DevToolsMessageWriter(pipe.ToBrowser);
        var reader = new DevToolsMessageReader(pipe.FromBrowser);
        var message = Encoding.UTF8.GetBytes("{\"id\":2,\"method\":\"Browser.getVersion\"}");
        await writer.WriteAsync(message, ct);

        Assert.Equal(message, await reader.ReadAsync(ct).AsTask().WaitAsync(_bound, ct));
        await host.StopAsync(process, ct);
    }

    [Fact]
    public async Task Descriptors_3_and_4_survive_cage()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        Assert.SkipWhen(!File.Exists("/usr/bin/cage"), "/usr/bin/cage is not installed.");
        PrepareSlot();
        var prefix = PoolCommandLine.SandboxPrefix(_pool, _slot, headed: true);
        var probe = Run([.. prefix, "/usr/bin/id", "-u"]);
        Assert.SkipWhen(probe.Exit != 0 && UserNamespaceDenied(probe.Err), "Unprivileged user namespaces are refused here: " + probe.Err.Trim());

        var (set, process, _, _) = StartThroughHelper([.. prefix, _pool.CagePath, "--", "/usr/bin/stat", "-L", "-c", "%F", "/proc/self/fd/3", "/proc/self/fd/4"]);
        using (set)
        {
            var r = await FinishAsync(process);
            Assert.SkipWhen(r.Exit != 0 && !r.Out.Contains("fifo", StringComparison.Ordinal), "cage could not run here: " + r.Err.Trim());
            Assert.Equal("fifo\nfifo", r.Out.Trim().ReplaceLineEndings("\n"));
        }
    }

    [Fact]
    public async Task Real_chromium_answers_DevTools_commands_over_the_pipe_through_the_whole_launch_chain()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        Assert.SkipWhen(!File.Exists(_chrome), _chrome + " is not present.");
        var ct = TestContext.Current.CancellationToken;
        PrepareSlot();
        var probe = Run([.. PoolCommandLine.SandboxPrefix(_pool, _slot, headed: false), "/usr/bin/id", "-u"]);
        Assert.SkipWhen(probe.Exit != 0 && UserNamespaceDenied(probe.Err), "Unprivileged user namespaces are refused here: " + probe.Err.Trim());

        var host = PipeHost();
        _touchedTable = true;
        var spec = PoolCommandLine.Build(_pool, "chrome" + _unique, _slot, new PoolSessionRequest("real-agent"), new PoolEgressEndpoint("127.0.0.1", 3128));
        await host.ApplyFirewallAsync([spec.Firewall], ct);

        var process = await host.StartAsync(spec, ct);
        try
        {
            Assert.True(host.TryGetPipe(spec.SessionId, out var pipe));
            using var writer = new DevToolsMessageWriter(pipe.ToBrowser);
            var reader = new DevToolsMessageReader(pipe.FromBrowser);
            await writer.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.getVersion\"}"), ct);

            string? reply = null;
            var deadline = DateTime.UtcNow + _bound;
            while (DateTime.UtcNow < deadline)
            {
                var message = await reader.ReadAsync(ct).AsTask().WaitAsync(_bound, ct);
                Assert.NotNull(message);
                var text = Encoding.UTF8.GetString(message);
                if (text.Contains("\"id\":1", StringComparison.Ordinal))
                {
                    reply = text;
                    break;
                }
            }

            Assert.NotNull(reply);
            Assert.Contains("\"product\"", reply, StringComparison.Ordinal);
            Assert.DoesNotContain("--no-sandbox", string.Join(' ', spec.Launch), StringComparison.Ordinal);
            Assert.DoesNotContain("--remote-debugging-port", string.Join(' ', spec.Launch), StringComparison.Ordinal);
        }
        finally
        {
            await host.StopAsync(process, ct);
            await host.RunAllAsync(spec.Cleanup, ct);
        }

        Assert.False(host.TryGetPipe(spec.SessionId, out _));
        Assert.Empty(Directory.GetFileSystemEntries(_pipeDir));
    }
}
