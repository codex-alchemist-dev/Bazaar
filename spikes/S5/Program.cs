// S5: one Orleans grain per agent vs a plain hosted service, 20 idle agents.
// Each invocation is one run in its own process, so RSS is not shared with other runs.
// Usage: dotnet run --project spikes/S5 -- --mode hosted|orleans [--agents 20] [--wakes 300]
//                                         [--idle-seconds 10] [--seed 42]
// Prints one line: "S5_RESULT {json}".
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Hosting;

internal sealed record Options(string Mode, int Agents, int Wakes, int IdleSeconds, int Seed)
{
    public static Options Parse(string[] a)
    {
        string mode = "hosted";
        int agents = 20, wakes = 300, idle = 10, seed = 42;
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--mode": mode = a[++i]; break;
                case "--agents": agents = int.Parse(a[++i]); break;
                case "--wakes": wakes = int.Parse(a[++i]); break;
                case "--idle-seconds": idle = int.Parse(a[++i]); break;
                case "--seed": seed = int.Parse(a[++i]); break;
                default: throw new ArgumentException($"unknown argument {a[i]}");
            }
        }
        if (mode is not ("hosted" or "orleans")) throw new ArgumentException($"unknown mode {mode}");
        return new Options(mode, agents, wakes, idle, seed);
    }
}

internal sealed record WakeRequest(TaskCompletionSource<int> Done);

// Plain hosted service: one inbox channel and one lease per agent, one consumer loop per agent.
internal sealed class HostedAgents : BackgroundService
{
    private readonly Channel<WakeRequest>[] _inbox;
    private readonly SemaphoreSlim[] _lease;
    private readonly int[] _turns;

    public HostedAgents(int agents)
    {
        _inbox = new Channel<WakeRequest>[agents];
        _lease = new SemaphoreSlim[agents];
        _turns = new int[agents];
        for (int i = 0; i < agents; i++)
        {
            _inbox[i] = Channel.CreateUnbounded<WakeRequest>(new UnboundedChannelOptions { SingleReader = true });
            _lease[i] = new SemaphoreSlim(1, 1);
        }
    }

    public async Task<int> WakeAsync(int agent)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _inbox[agent].Writer.WriteAsync(new WakeRequest(tcs));
        return await tcs.Task;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _inbox.Length).Select(i => RunAgentAsync(i, stoppingToken)));

    private async Task RunAgentAsync(int agent, CancellationToken ct)
    {
        try
        {
            await foreach (var req in _inbox[agent].Reader.ReadAllAsync(ct))
            {
                await _lease[agent].WaitAsync(ct);
                try
                {
                    // The turn: trivial in-memory work, identical to the grain turn below.
                    _turns[agent]++;
                    req.Done.TrySetResult(_turns[agent]);
                }
                finally
                {
                    _lease[agent].Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}

// Orleans: one grain activation per agent.
public interface IAgentGrain : IGrainWithIntegerKey
{
    Task<int> TurnAsync();
}

public sealed class AgentGrain : Grain, IAgentGrain
{
    private int _turns;

    public Task<int> TurnAsync()
    {
        _turns++;
        return Task.FromResult(_turns);
    }
}

internal sealed record RunResult(
    string Mode,
    string Os,
    string Runtime,
    int Agents,
    int Wakes,
    int IdleSeconds,
    double StartupMs,
    double IdleWorkingSetMb,
    double IdlePrivateMb,
    double WakeP50Ms,
    double WakeP95Ms,
    double WakeMaxMs);

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Options opts;
        try
        {
            opts = Options.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"usage error: {ex.Message}");
            return 2;
        }

        try
        {
            IHost host;
            Func<int, Task> wake;
            if (opts.Mode == "hosted")
            {
                var hosted = new HostedAgents(opts.Agents);
                host = Host.CreateDefaultBuilder()
                    .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
                    .ConfigureServices(s => s.AddHostedService(_ => hosted))
                    .Build();
                wake = a => hosted.WakeAsync(a);
            }
            else
            {
                host = Host.CreateDefaultBuilder()
                    .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
                    .UseOrleans(silo => silo.UseLocalhostClustering())
                    .Build();
                var grains = host.Services.GetRequiredService<IGrainFactory>();
                wake = a => grains.GetGrain<IAgentGrain>(a).TurnAsync();
            }

            var result = await RunAsync(opts, host, wake);
            Console.WriteLine("S5_RESULT " + JsonSerializer.Serialize(result));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("S5_ERROR " + ex);
            return 1;
        }
    }

    private static async Task<RunResult> RunAsync(Options o, IHost host, Func<int, Task> wake)
    {
        var startup = Stopwatch.StartNew();
        await host.StartAsync();

        // Activate all agents once so they are resident (Orleans activates on first call).
        for (int a = 0; a < o.Agents; a++)
        {
            await wake(a);
        }
        double startupMs = startup.Elapsed.TotalMilliseconds;

        await Task.Delay(TimeSpan.FromSeconds(o.IdleSeconds));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using (var proc = Process.GetCurrentProcess())
        {
            proc.Refresh();
            double wsMb = proc.WorkingSet64 / 1048576.0;
            double privMb = proc.PrivateMemorySize64 / 1048576.0;

            var rng = new Random(o.Seed);
            var lat = new double[o.Wakes];
            for (int k = 0; k < o.Wakes; k++)
            {
                int agent = rng.Next(o.Agents);
                long t0 = Stopwatch.GetTimestamp();
                await wake(agent);
                lat[k] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            Array.Sort(lat);
            await host.StopAsync();

            return new RunResult(
                Mode: o.Mode,
                Os: RuntimeInformation.OSDescription,
                Runtime: RuntimeInformation.FrameworkDescription,
                Agents: o.Agents,
                Wakes: o.Wakes,
                IdleSeconds: o.IdleSeconds,
                StartupMs: Math.Round(startupMs, 1),
                IdleWorkingSetMb: Math.Round(wsMb, 1),
                IdlePrivateMb: Math.Round(privMb, 1),
                WakeP50Ms: Math.Round(Percentile(lat, 0.50), 3),
                WakeP95Ms: Math.Round(Percentile(lat, 0.95), 3),
                WakeMaxMs: Math.Round(lat[^1], 3));
        }
    }

    private static double Percentile(double[] sorted, double p)
    {
        int idx = Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1);
        return sorted[idx];
    }
}
