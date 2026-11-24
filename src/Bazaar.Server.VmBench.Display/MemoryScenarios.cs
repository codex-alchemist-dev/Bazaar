using Bazaar.Server.BrowserPool.Abstractions;
using static Bazaar.Server.VmBench.Display.DisplayBenchSupport;

namespace Bazaar.Server.VmBench.Display;

/// <summary>
/// <see cref="DisplayBenchScenario.DesktopIdleRam"/>: with the kiosk profile up and no browser, waits the settle time and records
/// guest memory in use. Passes when it is at most <see cref="DisplayBenchCriteriaSnapshot.DesktopIdleMaxMiB"/>.
/// </summary>
public sealed class DesktopIdleRamScenario : IDisplayBenchScenario
{
    private const string _unit = "MiB";
    private readonly IDisplayBenchTargets? _targets;
    private readonly IDisplayBenchMemory? _memory;

    public DesktopIdleRamScenario(IDisplayBenchTargets? targets = null, IDisplayBenchMemory? memory = null)
    {
        _targets = targets;
        _memory = memory;
    }

    public DisplayBenchScenario Id => DisplayBenchScenario.DesktopIdleRam;

    public async Task<IReadOnlyList<DisplayBenchResult>> RunAsync(DisplayBenchContext context, CancellationToken ct)
    {
        const string variant = "kiosk";
        if (_targets is null || _memory is null)
        {
            return [NotRun(Id, variant, _unit, "No benchmark targets or memory reading are registered on this host.")];
        }

        try
        {
            var target = await _targets.KioskAsync(ct);
            if (target is null)
            {
                return [NotRun(Id, variant, _unit, "No kiosk VM is set up for the benchmark.")];
            }

            await Task.Delay(TimeSpan.FromSeconds(context.Criteria.SettleSeconds), context.Time, ct);
            var mib = await _memory.ReadGuestMemoryInUseBytesAsync(target, ct) / BytesPerMiB;
            var max = context.Criteria.DesktopIdleMaxMiB;
            var outcome = AtMost(mib, max);
            return [new DisplayBenchResult(Id, variant, outcome, mib, _unit, max, null, [],
                outcome == DisplayBenchOutcome.Passed ? null : $"Memory in use {mib:F1} MiB is above {max:F1} MiB.")];
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return [Failed(Id, variant, _unit, Describe(ex))];
        }
    }
}

/// <summary>
/// <see cref="DisplayBenchScenario.BrowserRam"/>: reads the browser host's memory, opens one tab (headless, then headed), waits the
/// settle time, reads again and closes it. The compared value is the rise per tab, against the headless or headed limit.
/// A pool that refuses the open (disabled, full, no egress) makes the variant "not run".
/// </summary>
public sealed class BrowserRamScenario : IDisplayBenchScenario
{
    private const string _unit = "MiB";
    private const string _benchAgent = "bench-display";
    private readonly IDisplayBenchTargets? _targets;
    private readonly IDisplayBenchMemory? _memory;
    private readonly IBrowserPool? _pool;

    public BrowserRamScenario(IDisplayBenchTargets? targets = null, IDisplayBenchMemory? memory = null, IBrowserPool? pool = null)
    {
        _targets = targets;
        _memory = memory;
        _pool = pool;
    }

    public DisplayBenchScenario Id => DisplayBenchScenario.BrowserRam;

    public async Task<IReadOnlyList<DisplayBenchResult>> RunAsync(DisplayBenchContext context, CancellationToken ct)
    {
        var results = new List<DisplayBenchResult>(2);
        foreach (var mode in new[] { PoolDisplayMode.Headless, PoolDisplayMode.Headed })
        {
            results.Add(await RunVariantAsync(mode, context, ct));
        }

        return results;
    }

    private async Task<DisplayBenchResult> RunVariantAsync(PoolDisplayMode mode, DisplayBenchContext context, CancellationToken ct)
    {
        var variant = mode == PoolDisplayMode.Headed ? "headed" : "headless";
        if (_targets is null || _memory is null || _pool is null)
        {
            return NotRun(Id, variant, _unit, "No benchmark targets, memory reading or browser pool are registered on this host.");
        }

        string? sessionId = null;
        try
        {
            var target = await _targets.BrowserHostAsync(ct);
            if (target is null)
            {
                return NotRun(Id, variant, _unit, "No browser host VM is set up for the benchmark.");
            }

            var baseline = await _memory.ReadGuestMemoryInUseBytesAsync(target, ct) / BytesPerMiB;
            try
            {
                sessionId = (await _pool.OpenAsync(new PoolSessionRequest(_benchAgent, mode), ct)).SessionId;
            }
            catch (BrowserPoolException ex)
            {
                return NotRun(Id, variant, _unit, $"The pool refused the session ({ex.Failure}).");
            }

            await Task.Delay(TimeSpan.FromSeconds(context.Criteria.SettleSeconds), context.Time, ct);
            var withTab = await _memory.ReadGuestMemoryInUseBytesAsync(target, ct) / BytesPerMiB;
            var delta = withTab - baseline;
            var max = mode == PoolDisplayMode.Headed ? context.Criteria.BrowserHeadedMaxDeltaMiB : context.Criteria.BrowserHeadlessMaxDeltaMiB;
            var outcome = AtMost(delta, max);
            return new DisplayBenchResult(Id, variant, outcome, delta, _unit, max, null,
                [new DisplayBenchMetric("baselineMiB", baseline, _unit), new DisplayBenchMetric("withTabMiB", withTab, _unit)],
                outcome == DisplayBenchOutcome.Passed ? null : $"One tab raised memory by {delta:F1} MiB, above {max:F1} MiB.");
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return Failed(Id, variant, _unit, Describe(ex));
        }
        finally
        {
            if (sessionId is not null)
            {
                try
                {
                    await _pool!.CloseAsync(sessionId, CancellationToken.None);
                }
                catch (Exception)
                {
                    // Closing is best effort; the pool's own idle and lifetime limits end a session that is left behind.
                }
            }
        }
    }
}
