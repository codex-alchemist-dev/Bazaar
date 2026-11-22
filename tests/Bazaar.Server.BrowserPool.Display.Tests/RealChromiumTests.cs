using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.BrowserPool.LinuxHost;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.BrowserPool.Display.Tests;

/// <summary>
/// Real Chromium through the real launch chain (<c>bazaar-pool-spawn</c>, setpriv, bwrap, the Linux pool host), driven by
/// <see cref="PoolBrowserPageDriver"/>. Same gate as the other real pool tests: BAZAAR_TEST_REAL_NETNS=1, Linux, root, and the
/// Chromium build the host's tests use. The skip check runs before the pool options are built, because their validator rejects
/// paths that are not Linux paths.
/// <para>
/// A pool start URL must be http or https, so the test serves the page itself: a tiny HTTP server on loopback is the session's
/// egress proxy (the browser sends every request, loopback included, to the proxy), and it records the requests the page makes
/// when its input receives text and its button is clicked. That shows the input really reached the page.
/// </para>
/// </summary>
public sealed partial class RealChromiumTests : IDisposable
{
    private const string _table = "bazaar_pool_displaytest";
    private const string _chrome = "/opt/pw-browsers/chromium-1243/chrome-linux64/chrome";
    private const int _slot = 2;

    private const string _page =
        "<!doctype html><html><head><title>t</title></head><body style=\"margin:0;background:#fff\">"
        + "<input id=i autofocus style=\"position:absolute;left:100px;top:100px;width:300px;height:40px;font-size:24px\">"
        + "<button id=b style=\"position:absolute;left:100px;top:200px;width:200px;height:60px\">Go</button>"
        + "<script>document.getElementById('i').addEventListener('input',function(e){fetch('/typed?len='+e.target.value.length)});"
        + "document.getElementById('b').addEventListener('click',function(){fetch('/clicked')});</script></body></html>";

    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(60);

    private readonly string _unique = Guid.NewGuid().ToString("N")[..12];
    private readonly string _temp;
    private readonly string _pipeDir;
    private readonly List<string> _requests = [];
    private readonly Lock _requestLock = new();
    private TcpListener? _server;
    private bool _touchedTable;
    private BrowserPoolOptions? _pool;

    public RealChromiumTests()
    {
        _temp = Path.Combine("/tmp", "bazaar-display-test-" + _unique);
        _pipeDir = "/run/bazaar-display-test-" + _unique + "/pipes";
    }

    private static string HelperPath => Path.Combine(AppContext.BaseDirectory, "bazaar-pool-spawn");

    public void Dispose()
    {
        _server?.Stop();
        if (SkipReason() is not null || _pool is null)
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
        Run(["rm", "-rf", "--one-file-system", "/run/bazaar-display-test-" + _unique]);
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

        foreach (var tool in new[] { "/usr/sbin/nft", "/usr/bin/stat", "/usr/bin/dd", "/usr/bin/readlink", HelperPath, _chrome })
        {
            if (!File.Exists(tool))
            {
                return tool + " is not installed.";
            }
        }

        // The host option accepts only plain absolute paths; say so here rather than fail inside it.
        return SafePath().IsMatch(HelperPath) && !HelperPath.Contains("..", StringComparison.Ordinal) ? null : "The test output path has characters the host option refuses: " + HelperPath;
    }

    [GeneratedRegex("^/[A-Za-z0-9._+/-]+$")]
    private static partial Regex SafePath();

    // The drop covers the pool's real slot uids (SlotUidBase 20000, MaxSessions 4). It once covered an unrelated range, so the
    // slot was never dropped; the active firewall probe is what showed it.
    private static string Render(IReadOnlyList<PoolFirewallRule> rules)
    {
        var accepts = string.Concat(rules.Select(r => $"    meta skuid {r.Uid} ip daddr {r.ProxyHost} tcp dport {r.ProxyPort} accept\n"));
        return $"table inet {_table}\ndelete table inet {_table}\ntable inet {_table} {{\n  chain output {{\n    type filter hook output priority 0; policy accept;\n"
            + accepts
            + "    meta skuid >= 20000 meta skuid <= 20003 drop\n  }\n"
            + "  chain input {\n    type filter hook input priority 0; policy accept;\n    iifname \"bzv*\" drop\n  }\n"
            + "  chain forward {\n    type filter hook forward priority 0; policy accept;\n    iifname \"bzv*\" drop\n  }\n}\n";
    }

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

    // The loopback server is both the page's origin and the browser's egress proxy: every request arrives here as "GET http://host/path".
    private async Task ServeAsync(TcpListener server)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await server.AcceptTcpClientAsync();
            }
            catch (Exception e) when (e is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var buffer = new byte[4096];
                while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal) && head.Length < 65536)
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0)
                    {
                        return;
                    }

                    head.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }

                var parts = head.ToString().Split(' ', 3);
                var target = parts.Length > 1 ? parts[1] : string.Empty;
                var path = Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.PathAndQuery : target;
                lock (_requestLock)
                {
                    _requests.Add(path);
                }

                var isPage = path == "/";
                var body = isPage ? Encoding.UTF8.GetBytes(_page) : [];
                var status = isPage ? "200 OK" : path.StartsWith("/typed", StringComparison.Ordinal) || path == "/clicked" ? "204 No Content" : "404 Not Found";
                var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(body);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private bool Saw(string path)
    {
        lock (_requestLock)
        {
            return _requests.Contains(path);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + _bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for " + what + ".");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Real_chromium_gives_a_screenshot_and_takes_clicks_and_typing_through_the_driver()
    {
        Assert.SkipWhen(SkipReason() is not null, SkipReason() ?? string.Empty);
        var ct = TestContext.Current.CancellationToken;

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

        Directory.CreateDirectory(_temp + "/pool");
        Run(["chmod", "0755", _temp]);
        Run(["chmod", "0711", _temp + "/pool"]);
        foreach (var c in PoolCommandLine.PrepareCommands(PoolCommandLine.SlotUid(_pool, _slot), PoolCommandLine.SlotDirectory(_pool, _slot)))
        {
            var r = Run(c.Argv);
            Assert.True(r.Exit == 0, "prepare failed: " + r.Err);
        }

        var probe = Run([.. PoolCommandLine.SandboxPrefix(_pool, _slot, headed: false), "/usr/bin/id", "-u"]);
        Assert.SkipWhen(probe.Exit != 0 && UserNamespaceDenied(probe.Err), "Unprivileged user namespaces are refused here: " + probe.Err.Trim());

        _server = new TcpListener(IPAddress.Loopback, 0);
        _server.Start();
        var port = ((IPEndPoint)_server.LocalEndpoint).Port;
        _ = ServeAsync(_server);

        // The firewall probe (doc 59 section 7) runs the native bazaar-pool-spawn as the slot's uid, which cannot execute the
        // framework-dependent test build. With BAZAAR_TEST_POOL_SPAWN_NATIVE set, the probe is on, against a world-readable copy; without it,
        // it is off for this test only, so the page-driver coverage still runs.
        var native = Environment.GetEnvironmentVariable("BAZAAR_TEST_POOL_SPAWN_NATIVE");
        var probeTool = _temp + "/bazaar-pool-spawn";
        var probeOn = OperatingSystem.IsLinux() && !string.IsNullOrEmpty(native) && File.Exists(native);
        if (probeOn && OperatingSystem.IsLinux())
        {
            File.Copy(native!, probeTool);
            File.SetUnixFileMode(probeTool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var host = new LinuxPoolVmHost(
            Render,
            new LinuxPoolHostOptions
            {
                TableName = _table,
                NftPath = "/usr/sbin/nft",
                FirewallTimeout = _bound,
                PoolSpawnHelperPath = HelperPath,
                PipeDirectory = _pipeDir,
                ProbeEnabled = probeOn,
                ProbeToolPath = probeOn ? probeTool : null,
            });
        _touchedTable = true;
        var sessionId = "display" + _unique;
        var spec = PoolCommandLine.Build(_pool, sessionId, _slot, new PoolSessionRequest("real-agent", StartUrl: "http://page.test/"), new PoolEgressEndpoint("127.0.0.1", port));
        await host.ApplyFirewallAsync([spec.Firewall], ct);

        var process = await host.StartAsync(spec, ct);
        try
        {
            await using var driver = new PoolBrowserPageDriver(host, new BrowserPageDriverOptions { CommandTimeout = TimeSpan.FromSeconds(30) });
            var target = new DisplayTarget("real-agent", Guid.NewGuid(), DisplayGuestOs.Linux, "pool", sessionId);
            Assert.True(driver.Supports(target));
            Assert.False(driver.Supports(target with { BrowserSessionId = "not-a-session" }));

            await WaitForAsync(() => Saw("/"), "the page request to reach the egress stand-in");
            // Let the page build; the screenshot call itself also attaches the first time.
            var first = await driver.CaptureScreenshotPngAsync(sessionId, ct);
            var (width, height) = ReadDimensions(first);
            Assert.InRange(width, 320, DisplayLimits.CeilingWidth);
            Assert.InRange(height, 240, DisplayLimits.CeilingHeight);
            Assert.InRange(first.Length, 100, 32 * 1024 * 1024);
            TestContext.Current.SendDiagnosticMessage($"real screenshot {width}x{height}, {first.Length} bytes");

            // Click the input (CSS pixels of the viewport), type into it, then click the button.
            await driver.DispatchInputAsync(sessionId, [new PointerClick(150, 120)], ct);
            await driver.DispatchInputAsync(sessionId, [new TypeText("hello")], ct);
            await WaitForAsync(() => Saw("/typed?len=5"), "the page to report 5 typed characters");
            await driver.DispatchInputAsync(sessionId, [new KeyStroke("Backspace")], ct);
            await WaitForAsync(() => Saw("/typed?len=4"), "the page to report the Backspace");
            await driver.DispatchInputAsync(sessionId, [new PointerClick(150, 230)], ct);
            await WaitForAsync(() => Saw("/clicked"), "the page to report the button click");

            var second = await driver.CaptureScreenshotPngAsync(sessionId, ct);
            Assert.Equal(ReadDimensions(second), (width, height));
            Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(first)), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(second)));
        }
        finally
        {
            await host.StopAsync(process, ct);
            await host.RunAllAsync(spec.Cleanup, ct);
        }

        Assert.False(host.TryGetPipe(sessionId, out _));
    }

    private static (int Width, int Height) ReadDimensions(byte[] png)
    {
        Assert.True(png.AsSpan().StartsWith(CdpJson.PngSignature), "Not a PNG.");
        // The IHDR chunk is first: 4 bytes length, "IHDR", then width and height as big-endian 32-bit values.
        Assert.Equal("IHDR", Encoding.ASCII.GetString(png, 12, 4));
        return (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
    }
}
