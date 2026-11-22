using System.Collections.Concurrent;
using Bazaar.Server.VmBench.Abstractions;

namespace Bazaar.Server.VmBench;

/// <summary>
/// Runs the VM benchmark (docs/design/16-agent-vms-and-environments.md, "Testing") against an <see cref="IVmBenchHost"/>.
/// One pair is started per run and always retired. Each measurement is bounded by the step timeout; a failed or timed-out
/// measurement is recorded as a failed sample, never as a number. Caller cancellation propagates after the pair is retired.
/// </summary>
public sealed class VmBenchRunner : IVmBenchRunner
{
    private const double _bytesPerMiB = 1024d * 1024d;
    private static readonly TimeSpan _defaultStepTimeout = TimeSpan.FromMinutes(15);
    private static readonly int[] _defaultParallelPairCounts = [1, 2];

    /// <summary>Order the scenarios run in. LoadedRam starts a steady load that only RetireAsync stops, so it runs last.</summary>
    private static readonly VmBenchScenario[] _scenarioOrder =
    [
        VmBenchScenario.IdleRam,
        VmBenchScenario.BootToReady,
        VmBenchScenario.Save,
        VmBenchScenario.Restore,
        VmBenchScenario.Checkpoint,
        VmBenchScenario.ShareThroughput,
        VmBenchScenario.GodotHeadlessExport,
        VmBenchScenario.LinuxBuild,
        VmBenchScenario.ParallelPairs,
        VmBenchScenario.LoadedRam,
    ];

    private readonly TimeProvider _time;

    public VmBenchRunner(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public async Task<VmBenchReport> RunAsync(IVmBenchHost host, VmBenchOptions options, string hostLabel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostLabel);
        Validate(options);

        var ctx = new RunContext(host, options, options.StepTimeout ?? _defaultStepTimeout, ct);
        var startedAt = _time.GetUtcNow();
        IReadOnlyList<BenchGuest>? guests = null;

        try
        {
            guests = await host.StartPairAsync(ct);
            foreach (var scenario in SelectedScenarios(options))
            {
                ct.ThrowIfCancellationRequested();
                await RunScenarioAsync(scenario, guests, ctx);
            }
        }
        finally
        {
            if (guests is not null)
            {
                await host.RetireAsync(guests, CancellationToken.None);
            }
        }

        var samples = ctx.Samples.ToArray();
        return new VmBenchReport(
            RunId: Guid.NewGuid().ToString("N"),
            StartedAt: startedAt,
            FinishedAt: _time.GetUtcNow(),
            HostLabel: hostLabel,
            Options: options,
            Samples: samples,
            Stats: VmBenchStats.Compute(samples));
    }

    private static void Validate(VmBenchOptions options)
    {
        if (options.Iterations < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Iterations, "Iterations must be at least 1.");
        }

        if (options.WarmupIterations < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WarmupIterations, "WarmupIterations cannot be negative.");
        }

        if (options.ShareBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ShareBytes, "ShareBytes must be positive.");
        }

        if (options.StepTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), timeout, "StepTimeout must be positive.");
        }

        foreach (var count in options.ParallelPairCounts ?? _defaultParallelPairCounts)
        {
            if (count < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(options), count, "Parallel pair counts must be at least 1.");
            }
        }
    }

    private static IEnumerable<VmBenchScenario> SelectedScenarios(VmBenchOptions options)
    {
        if (options.Scenarios is not { Count: > 0 } selected)
        {
            return _scenarioOrder;
        }

        var wanted = selected.ToHashSet();
        return _scenarioOrder.Where(wanted.Contains);
    }

    private async Task RunScenarioAsync(VmBenchScenario scenario, IReadOnlyList<BenchGuest> guests, RunContext ctx)
    {
        switch (scenario)
        {
            case VmBenchScenario.IdleRam:
                foreach (var guest in guests)
                {
                    await MeasureGuestAsync(scenario, guest, "MiB", ct => ReadMemoryMiBAsync(ctx, guest, ct), ctx);
                }
                break;

            case VmBenchScenario.LoadedRam:
                foreach (var guest in guests)
                {
                    await MeasureLoadedRamAsync(guest, ctx);
                }
                break;

            case VmBenchScenario.BootToReady:
                foreach (var guest in guests)
                {
                    await MeasureGuestAsync(scenario, guest, "s", async ct => (await ctx.Host.BootToReadyAsync(guest, ct)).TotalSeconds, ctx);
                }
                break;

            case VmBenchScenario.Save:
                foreach (var guest in guests)
                {
                    await MeasureGuestAsync(scenario, guest, "s", async ct => (await ctx.Host.SaveAsync(guest, ct)).TotalSeconds, ctx);
                }
                break;

            case VmBenchScenario.Restore:
                foreach (var guest in guests)
                {
                    await MeasureGuestAsync(scenario, guest, "s", async ct => (await ctx.Host.RestoreAsync(guest, ct)).TotalSeconds, ctx);
                }
                break;

            case VmBenchScenario.Checkpoint:
                foreach (var guest in guests)
                {
                    await MeasureGuestAsync(scenario, guest, "s", async ct => (await ctx.Host.CheckpointAsync(guest, ct)).TotalSeconds, ctx);
                }
                break;

            case VmBenchScenario.ShareThroughput:
                foreach (var guest in guests)
                {
                    await MeasureGuestAsync(
                        scenario,
                        guest,
                        "MiB/s",
                        async ct => (await ctx.Host.MeasureShareThroughputAsync(guest, ctx.Options.ShareBytes, ct)) / _bytesPerMiB,
                        ctx);
                }
                break;

            case VmBenchScenario.GodotHeadlessExport:
                foreach (var guest in guests)
                {
                    await MeasureWorkloadAsync(scenario, guest, BenchWorkload.GodotHeadlessExport, ctx);
                }
                break;

            case VmBenchScenario.LinuxBuild:
                foreach (var guest in guests.Where(g => g.Os == BenchGuestOs.Linux))
                {
                    await MeasureWorkloadAsync(scenario, guest, BenchWorkload.LinuxBuild, ctx);
                }
                break;

            case VmBenchScenario.ParallelPairs:
                await MeasureParallelPairsAsync(ctx);
                break;
        }
    }

    private static async Task<double> ReadMemoryMiBAsync(RunContext ctx, BenchGuest guest, CancellationToken ct)
    {
        return (await ctx.Host.ReadMemoryBytesAsync(guest, ct)) / _bytesPerMiB;
    }

    private Task MeasureWorkloadAsync(VmBenchScenario scenario, BenchGuest guest, BenchWorkload workload, RunContext ctx)
    {
        return MeasureGuestAsync(
            scenario,
            guest,
            "s",
            async ct => (await ctx.Host.RunWorkloadAsync(guest, workload, ct)).TotalSeconds,
            ctx);
    }

    /// <summary>Warmup iterations run and are discarded; measured iterations are numbered 1..Iterations.</summary>
    private async Task MeasureGuestAsync(
        VmBenchScenario scenario,
        BenchGuest guest,
        string unit,
        Func<CancellationToken, Task<double>> step,
        RunContext ctx)
    {
        var warmup = ctx.Options.WarmupIterations;
        for (var i = 1; i <= warmup + ctx.Options.Iterations; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var outcome = await RunStepAsync(step, ctx);
            if (i > warmup)
            {
                Record(ctx, scenario, guest.Os, i - warmup, unit, outcome);
            }
        }
    }

    /// <summary>The steady load starts once per guest; if it cannot start, every measured iteration is recorded as failed.</summary>
    private async Task MeasureLoadedRamAsync(BenchGuest guest, RunContext ctx)
    {
        var load = await RunStepAsync(async ct =>
        {
            await ctx.Host.StartLoadAsync(guest, ct);
            return 0;
        }, ctx);

        var warmup = ctx.Options.WarmupIterations;
        for (var i = 1; i <= warmup + ctx.Options.Iterations; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var outcome = load.Ok
                ? await RunStepAsync(ct => ReadMemoryMiBAsync(ctx, guest, ct), ctx)
                : new StepOutcome(false, 0, $"load did not start: {load.Error}");
            if (i > warmup)
            {
                Record(ctx, VmBenchScenario.LoadedRam, guest.Os, i - warmup, "MiB", outcome);
            }
        }
    }

    /// <summary>
    /// For each count, starts that many extra pairs at once and times until all are started and booted. The sample is pair-level,
    /// so its Os is nominal (Windows, the first supported host guest); both guests of each pair are included in the time.
    /// The extra pairs are retired after each count, also after a failure or cancellation.
    /// </summary>
    private async Task MeasureParallelPairsAsync(RunContext ctx)
    {
        foreach (var count in ctx.Options.ParallelPairCounts ?? _defaultParallelPairCounts)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var extra = new ConcurrentBag<IReadOnlyList<BenchGuest>>();
            try
            {
                var started = _time.GetTimestamp();
                var outcome = await RunStepAsync(async ct =>
                {
                    var pairs = Enumerable.Range(0, count).Select(async _ =>
                    {
                        var pair = await ctx.Host.StartPairAsync(ct);
                        extra.Add(pair);
                        foreach (var guest in pair)
                        {
                            await ctx.Host.BootToReadyAsync(guest, ct);
                        }
                    });
                    await Task.WhenAll(pairs);
                    return _time.GetElapsedTime(started).TotalSeconds;
                }, ctx);

                Record(ctx, VmBenchScenario.ParallelPairs, BenchGuestOs.Windows, count, "s", outcome);
            }
            finally
            {
                foreach (var pair in extra)
                {
                    await ctx.Host.RetireAsync(pair, CancellationToken.None);
                }
            }
        }
    }

    /// <summary>
    /// Runs one measurement under the step timeout. A timeout or a host exception becomes a failed outcome. Caller
    /// cancellation is rethrown so the run stops; the pair is still retired by the caller's finally.
    /// </summary>
    private static async Task<StepOutcome> RunStepAsync(Func<CancellationToken, Task<double>> step, RunContext ctx)
    {
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        stepCts.CancelAfter(ctx.StepTimeout);
        try
        {
            return new StepOutcome(true, await step(stepCts.Token), null);
        }
        catch (OperationCanceledException) when (ctx.Ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (stepCts.IsCancellationRequested)
        {
            return new StepOutcome(false, 0, $"timed out after {ctx.StepTimeout}");
        }
        catch (Exception ex)
        {
            return new StepOutcome(false, 0, ex.Message);
        }
    }

    private void Record(RunContext ctx, VmBenchScenario scenario, BenchGuestOs os, int iteration, string unit, StepOutcome outcome)
    {
        var at = _time.GetUtcNow();
        ctx.Samples.Add(outcome.Ok
            ? new VmBenchSample(scenario, os, iteration, outcome.Value, unit, at)
            : new VmBenchSample(scenario, os, iteration, 0, unit, at, Failed: true, Note: outcome.Error));
    }

    private readonly record struct StepOutcome(bool Ok, double Value, string? Error);

    /// <summary>Per-run state shared by the scenario methods.</summary>
    private sealed class RunContext(IVmBenchHost host, VmBenchOptions options, TimeSpan stepTimeout, CancellationToken ct)
    {
        public IVmBenchHost Host { get; } = host;

        public VmBenchOptions Options { get; } = options;

        public CancellationToken Ct { get; } = ct;

        public TimeSpan StepTimeout { get; } = stepTimeout;

        public List<VmBenchSample> Samples { get; } = [];
    }
}
