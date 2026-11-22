using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Bazaar.EndToEnd.Tests.Harness;

/// <summary>
/// The real <c>Bazaar.Server.Host</c> as an operating-system process, started the way an operator starts it: <c>migrate</c> once,
/// then run. Configuration goes in as command-line keys and secret-reference environment variables, the same surface a
/// deployment uses. Nothing here loads server types, so a service-graph or startup failure shows up as a failed start.
/// </summary>
public sealed class HostProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _log = new();

    private HostProcess(Process process, int port)
    {
        _process = process;
        Port = port;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Enqueue(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public int Port { get; }

    public Uri WireUri => new($"ws://127.0.0.1:{Port}/ws");

    public Uri HttpBase => new($"http://127.0.0.1:{Port}/");

    public string Log => string.Join('\n', _log);

    /// <summary>Where the built host lives: the sibling output folder of this test project's own configuration.</summary>
    public static string HostDll()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Bazaar.slnx"))) dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException("Bazaar.slnx not found above the test binaries.");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name; // Release or Debug
        var dll = Path.Combine(dir.FullName, "src", "Bazaar.Server.Host", "bin", configuration, "net11.0", "Bazaar.Server.Host.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("The server host is not built.", dll);
    }

    public static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    /// <summary>Runs <c>migrate</c> to completion. Throws with the command's output when it fails.</summary>
    public static async Task MigrateAsync(IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        using var process = Create(["migrate", .. keys], env);
        var output = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"migrate exited {process.ExitCode}:\n{string.Join('\n', output)}");
    }

    /// <summary>Starts the host and waits until <c>/health/ready</c> answers 200. Throws with the host log when it does not.</summary>
    public static async Task<HostProcess> StartAsync(int port, IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken ct)
    {
        var process = Create(keys, env);
        process.Start();
        var host = new HostProcess(process, port);
        try
        {
            using var http = new HttpClient { BaseAddress = host.HttpBase, Timeout = TimeSpan.FromSeconds(5) };
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException($"The host exited with {process.ExitCode} before it was ready.\n{host.Log}");
                try
                {
                    using var response = await http.GetAsync("health/ready", ct);
                    if (response.StatusCode == HttpStatusCode.OK) return host;
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    // One slow probe.
                }

                await Task.Delay(250, ct);
            }

            throw new TimeoutException($"The host was not ready within {timeout}.\n{host.Log}");
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static Process Create(IEnumerable<string> arguments, IReadOnlyDictionary<string, string> env)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(HostDll());
        foreach (var a in arguments) info.ArgumentList.Add(a);
        foreach (var (k, v) in env) info.Environment[k] = v;
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";
        info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        return new Process { StartInfo = info };
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }
}
