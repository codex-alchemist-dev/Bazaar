using Bazaar.Server.VmDisplay.Abstractions;
using static Bazaar.Server.VmBench.Display.DisplayBenchSupport;

namespace Bazaar.Server.VmBench.Display;

/// <summary>Shared loop for the two per-source latency scenarios.</summary>
public abstract class SourceLatencyScenario : IDisplayBenchScenario
{
    private static readonly DisplaySourceKind[] _sources = [DisplaySourceKind.Hypervisor, DisplaySourceKind.Guest, DisplaySourceKind.Browser];
    private readonly IGuestDisplay? _display;
    private readonly IDisplayBenchTargets? _targets;

    protected SourceLatencyScenario(IGuestDisplay? display, IDisplayBenchTargets? targets)
    {
        _display = display;
        _targets = targets;
    }

    public abstract DisplayBenchScenario Id { get; }

    protected abstract int Samples(DisplayBenchCriteriaSnapshot criteria);

    protected abstract double P95Max(DisplayBenchCriteriaSnapshot criteria, DisplaySourceKind source);

    /// <summary>Performs one timed operation against the source; the elapsed time of this call is the sample.</summary>
    protected abstract Task<IReadOnlyList<DisplayBenchMetric>> OperateAsync(IGuestDisplay display, DisplayTarget target, DisplaySourceKind source, int index, CancellationToken ct);

    /// <summary>Runs once, untimed, before the samples (for example to let the guest source learn the output size).</summary>
    protected virtual Task PrepareAsync(IGuestDisplay display, DisplayTarget target, DisplaySourceKind source, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Verdict on the secondary numbers collected over all samples; a non-null text fails the variant.</summary>
    protected virtual string? CheckMetrics(DisplayBenchCriteriaSnapshot criteria, IReadOnlyList<DisplayBenchMetric> metrics) => null;

    public async Task<IReadOnlyList<DisplayBenchResult>> RunAsync(DisplayBenchContext context, CancellationToken ct)
    {
        var results = new List<DisplayBenchResult>(_sources.Length);
        foreach (var source in _sources)
        {
            results.Add(await RunSourceAsync(source, context, ct));
        }

        return results;
    }

    private async Task<DisplayBenchResult> RunSourceAsync(DisplaySourceKind source, DisplayBenchContext context, CancellationToken ct)
    {
        const string unit = "ms";
        var variant = SourceName(source);
        if (_display is null || _targets is null)
        {
            return NotRun(Id, variant, unit, "No guest display or benchmark targets are registered on this host.");
        }

        try
        {
            var target = await _targets.ForSourceAsync(source, ct);
            if (target is null)
            {
                return NotRun(Id, variant, unit, $"No target is set up for the {variant} source.");
            }

            if (!_display.AvailableSources(target).Contains(source))
            {
                return NotRun(Id, variant, unit, $"The {variant} source is not available for the target.");
            }

            await PrepareAsync(_display, target, source, ct);
            var millis = new List<double>();
            var extras = new List<DisplayBenchMetric>();
            var failures = 0;
            string? firstFailure = null;
            for (var i = 0; i < Samples(context.Criteria); i++)
            {
                ct.ThrowIfCancellationRequested();
                var start = context.Time.GetTimestamp();
                try
                {
                    var metrics = await OperateAsync(_display, target, source, i, ct);
                    millis.Add(context.Time.GetElapsedTime(start).TotalMilliseconds);
                    extras.AddRange(metrics);
                }
                catch (Exception ex) when (!IsCallerCancellation(ex, ct))
                {
                    failures++;
                    firstFailure ??= Describe(ex);
                }
            }

            var stat = Stat(millis, failures);
            var max = P95Max(context.Criteria, source);
            var summary = Summarise(extras);
            string? reason = null;
            if (failures > 0)
            {
                reason = $"{failures} of {Samples(context.Criteria)} attempts failed; first: {firstFailure}";
            }
            else if (stat.P95 > max)
            {
                reason = $"p95 {stat.P95:F1} ms is above {max:F1} ms.";
            }
            else
            {
                reason = CheckMetrics(context.Criteria, summary);
            }

            return new DisplayBenchResult(Id, variant, reason is null ? DisplayBenchOutcome.Passed : DisplayBenchOutcome.Failed,
                stat.Count == 0 ? null : stat.P95, unit, max, stat, summary, reason);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            return Failed(Id, variant, unit, Describe(ex));
        }
    }

    /// <summary>Folds per-sample metrics into one median and one maximum per name.</summary>
    private static List<DisplayBenchMetric> Summarise(List<DisplayBenchMetric> extras)
    {
        var summary = new List<DisplayBenchMetric>();
        foreach (var group in extras.GroupBy(m => (m.Name, m.Unit)))
        {
            var s = Stat(group.Select(m => m.Value), 0);
            summary.Add(new DisplayBenchMetric(group.Key.Name + "Median", s.Median, group.Key.Unit));
            summary.Add(new DisplayBenchMetric(group.Key.Name + "Max", s.Max, group.Key.Unit));
        }

        return summary;
    }
}

/// <summary>
/// <see cref="DisplayBenchScenario.ScreenshotLatency"/>: N captures per source, timed with the run's clock. Records the latency
/// distribution and the frame size in bytes and pixels, never the frame. A frame from another source than the one asked for is a failed sample.
/// </summary>
public sealed class ScreenshotLatencyScenario : SourceLatencyScenario
{
    public ScreenshotLatencyScenario(IGuestDisplay? display = null, IDisplayBenchTargets? targets = null)
        : base(display, targets)
    {
    }

    public override DisplayBenchScenario Id => DisplayBenchScenario.ScreenshotLatency;

    protected override int Samples(DisplayBenchCriteriaSnapshot criteria) => criteria.ScreenshotSamples;

    protected override double P95Max(DisplayBenchCriteriaSnapshot criteria, DisplaySourceKind source) => source switch
    {
        DisplaySourceKind.Hypervisor => criteria.ScreenshotHypervisorP95MaxMs,
        DisplaySourceKind.Guest => criteria.ScreenshotGuestP95MaxMs,
        _ => criteria.ScreenshotBrowserP95MaxMs,
    };

    protected override async Task<IReadOnlyList<DisplayBenchMetric>> OperateAsync(IGuestDisplay display, DisplayTarget target, DisplaySourceKind source, int index, CancellationToken ct)
    {
        var frame = await display.CaptureAsync(new CaptureRequest(target, source), ct);
        if (frame.Source != source)
        {
            throw new InvalidOperationException($"A {frame.Source} frame came back for the {source} source.");
        }

        return
        [
            new DisplayBenchMetric("frameBytes", frame.Png.Length, "bytes"),
            new DisplayBenchMetric("frameWidth", frame.Width, "px"),
            new DisplayBenchMetric("frameHeight", frame.Height, "px"),
        ];
    }

    protected override string? CheckMetrics(DisplayBenchCriteriaSnapshot criteria, IReadOnlyList<DisplayBenchMetric> metrics)
    {
        var largest = metrics.FirstOrDefault(m => m.Name == "frameBytesMax");
        return largest is not null && largest.Value > criteria.ScreenshotMaxFrameBytes
            ? $"A frame of {largest.Value:F0} bytes is above {criteria.ScreenshotMaxFrameBytes} bytes."
            : null;
    }
}

/// <summary>
/// <see cref="DisplayBenchScenario.InputLatency"/>: sends one harmless pointer move per sample and times the call until the source
/// acknowledges it by returning. One untimed capture runs first, because the guest pointer needs the output size a capture teaches it.
/// </summary>
public sealed class InputLatencyScenario : SourceLatencyScenario
{
    public InputLatencyScenario(IGuestDisplay? display = null, IDisplayBenchTargets? targets = null)
        : base(display, targets)
    {
    }

    public override DisplayBenchScenario Id => DisplayBenchScenario.InputLatency;

    protected override int Samples(DisplayBenchCriteriaSnapshot criteria) => criteria.InputSamples;

    protected override double P95Max(DisplayBenchCriteriaSnapshot criteria, DisplaySourceKind source) => source switch
    {
        DisplaySourceKind.Hypervisor => criteria.InputHypervisorP95MaxMs,
        DisplaySourceKind.Guest => criteria.InputGuestP95MaxMs,
        _ => criteria.InputBrowserP95MaxMs,
    };

    protected override async Task PrepareAsync(IGuestDisplay display, DisplayTarget target, DisplaySourceKind source, CancellationToken ct)
    {
        try
        {
            await display.CaptureAsync(new CaptureRequest(target, source), ct);
        }
        catch (Exception ex) when (!IsCallerCancellation(ex, ct))
        {
            // The warm-up is best effort; a source that cannot capture still gets its input timed and any failure shows there.
        }
    }

    protected override async Task<IReadOnlyList<DisplayBenchMetric>> OperateAsync(IGuestDisplay display, DisplayTarget target, DisplaySourceKind source, int index, CancellationToken ct)
    {
        InputAction[] actions = [new PointerMove(10 + (index % 2), 10)];
        await display.SendInputAsync(new InputBatch(target, actions, source), ct);
        return [];
    }
}
