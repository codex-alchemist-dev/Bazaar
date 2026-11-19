using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Tests;

/// <summary>
/// Runs the REAL argv from <see cref="PoolCommandLine"/> (real setpriv, bubblewrap and Chromium) on a Linux machine where the test is
/// root. Skipped, with the reason, when a tool or the Chromium path is missing, when not root, or when the kernel or its security
/// policy refuses unprivileged user namespaces for the dropped uid. It never falls back to a weaker sandbox.
/// </summary>
public sealed partial class RealSandboxTests : IDisposable
{
    private const string Chrome = "/opt/pw-browsers/chromium-1243/chrome-linux64/chrome";
    private const int Slot = 0;

    private readonly string _temp;
    private readonly BrowserPoolOptions _options;
    private readonly List<Process> _processes = [];

    public RealSandboxTests()
    {
        _temp = Path.Combine("/tmp", "bazaar-pool-test-" + Guid.NewGuid().ToString("N")[..12]);
        _options = new BrowserPoolOptions
        {
            SlotRoot = _temp + "/pool",
            ChromiumPath = Chrome,
            UseSystemdRun = false,
            MemoryMaxMb = 0,
            MaxSessions = 4,
            MaxSessionsPerAgent = 2,
            ExtraReadOnlyBinds = ["/opt/pw-browsers"],
        };
    }

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

        if (OperatingSystem.IsLinux() && Directory.Exists(_temp))
        {
            // Wipe the way the pool does, then remove the empty shell.
            foreach (var slot in new[] { 0, 1 })
            {
                foreach (var c in PoolCommandLine.CleanupCommands(PoolCommandLine.SlotUid(_options, slot), PoolCommandLine.SlotDirectory(_options, slot)))
                {
                    RunBlocking(c.Argv);
                }
            }

            RunBlocking(["rm", "-rf", "--one-file-system", _temp]);
        }
    }

    private static string? SkipReason()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Not Linux.";
        }

        if (!File.ReadAllText("/proc/self/status").Contains("Uid:\t0\t", StringComparison.Ordinal))
        {
            return "Not running as root (the pool launcher is root in the pool VM).";
        }

        foreach (var tool in new[] { "/usr/bin/bwrap", "/usr/bin/setpriv" })
        {
            if (!File.Exists(tool))
            {
                return tool + " is not installed.";
            }
        }

        return File.Exists(Chrome) ? null : Chrome + " is not present.";
    }

    private static (int Exit, string Out, string Err) RunBlocking(IReadOnlyList<string> argv, int timeoutMs = 30000)
    {
        var psi = new ProcessStartInfo(argv[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in argv.Skip(1))
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            p.Kill(true);
        }

        return (p.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private void Prepare(int slot)
    {
        Directory.CreateDirectory(_temp + "/pool");
        // The slot users must be able to walk down to their own slot directory (the slot root is root-owned, traverse-only).
        RunBlocking(["chmod", "0755", _temp]);
        RunBlocking(["chmod", "0711", _temp + "/pool"]);
        foreach (var c in PoolCommandLine.PrepareCommands(PoolCommandLine.SlotUid(_options, slot), PoolCommandLine.SlotDirectory(_options, slot)))
        {
            var r = RunBlocking(c.Argv);
            Assert.True(r.Exit == 0, "prepare failed: " + string.Join(' ', c.Argv) + " " + r.Err);
        }
    }

    [Fact]
    public void Sandbox_hides_the_host_and_runs_as_the_slot_uid()
    {
        var reason = SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        Prepare(0);
        Prepare(1);
        var prefix = PoolCommandLine.SandboxPrefix(_options, Slot, headed: false);

        var id = RunBlocking([.. prefix, "/usr/bin/id", "-u"]);
        Assert.SkipWhen(id.Exit != 0 && UserNamespaceDenied(id.Err), "Unprivileged user namespaces are refused here: " + id.Err.Trim());
        Assert.True(id.Exit == 0, id.Err);
        Assert.Equal("20000", id.Out.Trim());

        Assert.NotEqual(0, RunBlocking([.. prefix, "/usr/bin/ls", "/root"]).Exit);
        Assert.NotEqual(0, RunBlocking([.. prefix, "/usr/bin/ls", "/var"]).Exit);
        Assert.NotEqual(0, RunBlocking([.. prefix, "/usr/bin/ls", "/var/lib"]).Exit);
        Assert.NotEqual(0, RunBlocking([.. prefix, "/usr/bin/ls", "/run/user/0"]).Exit);

        // /home holds only the empty profile mount.
        var home = RunBlocking([.. prefix, "/usr/bin/ls", "-A", "/home"]);
        Assert.Equal("browser", home.Out.Trim());
        var profile = RunBlocking([.. prefix, "/usr/bin/ls", "-A", "/home/browser"]);
        Assert.Equal(string.Empty, profile.Out.Trim());

        // Neither the slot root nor another slot's directory exists inside the sandbox.
        Assert.NotEqual(0, RunBlocking([.. prefix, "/usr/bin/ls", _temp]).Exit);
        Assert.NotEqual(0, RunBlocking([.. prefix, "/usr/bin/ls", PoolCommandLine.SlotDirectory(_options, 1)]).Exit);
        Assert.NotEqual(0, RunBlocking([.. prefix, "/usr/bin/cat", "/etc/passwd"]).Exit);

        // The privileges are gone.
        var status = RunBlocking([.. prefix, "/usr/bin/grep", "-E", "^(NoNewPrivs|CapEff):", "/proc/self/status"]);
        Assert.Contains("NoNewPrivs:\t1", status.Out, StringComparison.Ordinal);
        Assert.Contains("CapEff:\t0000000000000000", status.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_chromium_runs_sandboxed_as_the_slot_uid_and_reaches_only_the_proxy()
    {
        var reason = SkipReason();
        Assert.SkipWhen(reason is not null, reason ?? string.Empty);
        Prepare(0);
        var probe = RunBlocking([.. PoolCommandLine.SandboxPrefix(_options, Slot, false), "/usr/bin/id", "-u"]);
        Assert.SkipWhen(probe.Exit != 0 && UserNamespaceDenied(probe.Err), "Unprivileged user namespaces are refused here: " + probe.Err.Trim());

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
                    var buffer = new byte[2048];
                    var n = await client.GetStream().ReadAsync(buffer);
                    var text = Encoding.ASCII.GetString(buffer, 0, n);
                    lines.Enqueue(text.Split('\n')[0].Trim());
                    await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                }
                catch (Exception) when (!listener.Server.IsBound)
                {
                    return;
                }
                catch (IOException)
                {
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }, TestContext.Current.CancellationToken);

        var spec = PoolCommandLine.Build(
            _options,
            "realtest",
            Slot,
            new PoolSessionRequest("real-agent"),
            new PoolEgressEndpoint("127.0.0.1", port));

        // Test-only shim: wire Chromium's --remote-debugging-pipe fds (3 reads commands, 4 writes replies) to stdin and stdout.
        var psi = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$@\" 3<&0 4>&1");
        psi.ArgumentList.Add("sh");
        foreach (var a in spec.Launch)
        {
            psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi)!;
        _processes.Add(proc);
        var stderr = proc.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var pipe = new CdpPipe(proc);

        var version = await pipe.CallAsync("Browser.getVersion", null, TimeSpan.FromSeconds(30));
        Assert.Contains("product", version, StringComparison.Ordinal);

        // (1) It stays alive and keeps answering.
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.False(proc.HasExited, "Chromium exited. stderr: " + (proc.HasExited ? await stderr : string.Empty));
        Assert.Contains("product", await pipe.CallAsync("Browser.getVersion", null, TimeSpan.FromSeconds(10)), StringComparison.Ordinal);

        // (4) Ask for a page; the dummy proxy must see the request, and the name is never resolved locally.
        await pipe.CallAsync("Target.createTarget", new { url = "http://example.invalid/" }, TimeSpan.FromSeconds(15));
        // Chromium may also send its own background requests (for example to a Google host); they reach the proxy too, which is the point.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!lines.Any(l => l.Contains("example.invalid", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Contains(lines, l => l.Contains("example.invalid", StringComparison.Ordinal));

        // (2) Every process of the slot uid: no --no-sandbox anywhere, privileges dropped, renderers under seccomp.
        var found = SlotProcesses(20000);
        Assert.NotEmpty(found);
        Assert.DoesNotContain(found, p => p.Cmdline.Contains("--no-sandbox", StringComparison.Ordinal));
        Assert.All(found, p => Assert.Equal("1", p.NoNewPrivs));
        var main = found.First(p => p.Cmdline.Contains("--remote-debugging-pipe", StringComparison.Ordinal) && !p.Cmdline.Contains("--type=", StringComparison.Ordinal) && !p.Cmdline.Contains("bwrap", StringComparison.Ordinal));
        Assert.Equal("20000", main.Uid);
        var renderers = found.Where(p => p.Cmdline.Contains("--type=renderer", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(renderers);
        Assert.All(renderers, r => Assert.Equal("2", r.Seccomp));

        // Close: stdin EOF ends the browser; cleanup leaves nothing of the slot behind.
        proc.StandardInput.Close();
        foreach (var c in spec.Cleanup)
        {
            RunBlocking(c.Argv);
        }

        Assert.False(Directory.Exists(PoolCommandLine.SlotDirectory(_options, Slot)));
        Assert.True(proc.WaitForExit(15000), "The launch process did not end after cleanup.");
        Assert.DoesNotContain(SlotProcesses(20000), p => p.Cmdline.Length > 0); // zombies awaiting their parent have an empty command line
        listener.Stop();
    }

    private static bool UserNamespaceDenied(string stderr) =>
        stderr.Contains("namespace", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("setting up uid map", StringComparison.OrdinalIgnoreCase);

    private sealed record ProcInfo(int Pid, string Cmdline, string Uid, string NoNewPrivs, string Seccomp);

    private static List<ProcInfo> SlotProcesses(int uid)
    {
        var list = new List<ProcInfo>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
            {
                continue;
            }

            try
            {
                var status = File.ReadAllText(dir + "/status");
                var realUid = StatusField(status, "Uid").Split('\t', StringSplitOptions.RemoveEmptyEntries)[0];
                if (realUid != uid.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    continue;
                }

                var cmd = File.ReadAllText(dir + "/cmdline").Replace('\0', ' ');
                list.Add(new ProcInfo(pid, cmd, realUid, StatusField(status, "NoNewPrivs").Trim(), StatusField(status, "Seccomp").Trim()));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    private static string StatusField(string status, string name)
    {
        var m = Regex.Match(status, "^" + name + ":\\s*(.*)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    /// <summary>NUL-terminated JSON over the browser's debugging pipe (stdin and stdout of the shim).</summary>
    private sealed class CdpPipe
    {
        private readonly Process _process;
        private readonly System.Threading.Channels.Channel<string> _messages = System.Threading.Channels.Channel.CreateUnbounded<string>();
        private int _nextId;

        public CdpPipe(Process process)
        {
            _process = process;
            var ct = TestContext.Current.CancellationToken;
            _ = Task.Run(async () =>
            {
                var stream = process.StandardOutput.BaseStream;
                var buffer = new byte[8192];
                var current = new MemoryStream();
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, ct);
                    if (n == 0)
                    {
                        _messages.Writer.TryComplete();
                        return;
                    }

                    for (var i = 0; i < n; i++)
                    {
                        if (buffer[i] == 0)
                        {
                            _messages.Writer.TryWrite(Encoding.UTF8.GetString(current.ToArray()));
                            current.SetLength(0);
                        }
                        else
                        {
                            current.WriteByte(buffer[i]);
                        }
                    }
                }
            });
        }

        public async Task<string> CallAsync(string method, object? parameters, TimeSpan timeout)
        {
            var id = Interlocked.Increment(ref _nextId);
            var json = JsonSerializer.Serialize(new Dictionary<string, object?> { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new { } });
            var bytes = Encoding.UTF8.GetBytes(json + "\0");
            await _process.StandardInput.BaseStream.WriteAsync(bytes);
            await _process.StandardInput.BaseStream.FlushAsync();

            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var message = await _messages.Reader.ReadAsync(cts.Token);
                using var doc = JsonDocument.Parse(message);
                if (doc.RootElement.TryGetProperty("id", out var got) && got.GetInt32() == id)
                {
                    return message;
                }
            }
        }
    }
}
