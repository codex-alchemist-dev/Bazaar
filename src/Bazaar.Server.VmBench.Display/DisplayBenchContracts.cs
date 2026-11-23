using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmBench.Display;

/// <summary>
/// The display and density scenarios of doc 59 section 10. The core harness's scenario list is a closed enum, so these live in
/// this module and are run by <see cref="IDisplayBenchRunner"/>.
/// </summary>
public enum DisplayBenchScenario
{
    /// <summary>Kiosk profile up, no browser: guest memory in use after a settle time.</summary>
    DesktopIdleRam,

    /// <summary>One browser tab, headed and headless: guest memory in use and the rise over the baseline.</summary>
    BrowserRam,

    /// <summary>Time to capture a frame, per display source, plus frame size.</summary>
    ScreenshotLatency,

    /// <summary>Time from sending an input action to the source acknowledging it, per source.</summary>
    InputLatency,

    /// <summary>VM pairs admitted by the density governor before it queues, and host free memory at that point.</summary>
    DensityRamp,
}

/// <summary>The outcome of one measured variant. A variant that could not run is never a pass.</summary>
public enum DisplayBenchOutcome
{
    /// <summary>Measured and within the criteria.</summary>
    Passed,

    /// <summary>Measured and outside the criteria, or a measurement failed.</summary>
    Failed,

    /// <summary>Could not run (a source, port or governor is missing); the reason says which.</summary>
    NotRun,
}

/// <summary>A secondary number recorded with a result, for example the frame size or host free memory.</summary>
public sealed record DisplayBenchMetric(string Name, double Value, string Unit);

/// <summary>Summary of the successful samples of one variant (nearest-rank p95, as in the core harness).</summary>
public sealed record DisplayBenchStat(int Count, int Failures, double Min, double Median, double P95, double Max, double Mean);

/// <summary>
/// One variant of one scenario. <paramref name="Value"/> is what the verdict compared with <paramref name="Threshold"/> (a maximum,
/// except for the density ramp where it is a minimum). Nothing here holds frame bytes: sizes and timings only.
/// </summary>
/// <param name="Scenario">The scenario.</param>
/// <param name="Variant">What was measured: <c>kiosk</c>, <c>headed</c>, <c>headless</c>, or a source name.</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="Value">The compared value, or null when nothing was measured.</param>
/// <param name="Unit">The unit of <paramref name="Value"/>.</param>
/// <param name="Threshold">The criterion that was applied.</param>
/// <param name="Stat">The latency distribution, for the latency scenarios.</param>
/// <param name="Metrics">Secondary numbers.</param>
/// <param name="Reason">Why a variant did not run or failed; null on a pass.</param>
public sealed record DisplayBenchResult(
    DisplayBenchScenario Scenario,
    string Variant,
    DisplayBenchOutcome Outcome,
    double? Value,
    string Unit,
    double? Threshold,
    DisplayBenchStat? Stat,
    IReadOnlyList<DisplayBenchMetric> Metrics,
    string? Reason);

/// <summary>The finished run: the criteria that were frozen at its start, then the results.</summary>
public sealed record DisplayBenchReport(
    string RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string HostLabel,
    DisplayBenchCriteriaSnapshot Criteria,
    IReadOnlyList<DisplayBenchResult> Results,
    DisplayBenchOutcome Overall);

/// <summary>What one scenario sees: the frozen criteria and the clock.</summary>
public sealed record DisplayBenchContext(DisplayBenchCriteriaSnapshot Criteria, TimeProvider Time);

/// <summary>One doc 59 scenario. It never throws for a failed measurement; it reports it. Caller cancellation propagates.</summary>
public interface IDisplayBenchScenario
{
    /// <summary>Which scenario this is.</summary>
    DisplayBenchScenario Id { get; }

    /// <summary>Runs every variant of the scenario.</summary>
    Task<IReadOnlyList<DisplayBenchResult>> RunAsync(DisplayBenchContext context, CancellationToken ct);
}

/// <summary>Runs the registered display scenarios against the host's ports and returns the report.</summary>
public interface IDisplayBenchRunner
{
    /// <summary>Snapshots and validates the criteria, runs the selected scenarios (empty or null means all) and returns the report.</summary>
    Task<DisplayBenchReport> RunAsync(string hostLabel, IReadOnlyList<DisplayBenchScenario>? scenarios, CancellationToken ct);
}

/// <summary>Renders a report as Markdown for people and JSON for tools. Both carry the criteria.</summary>
public interface IDisplayBenchReportWriter
{
    /// <summary>Renders Markdown.</summary>
    string ToMarkdown(DisplayBenchReport report);

    /// <summary>Renders JSON.</summary>
    string ToJson(DisplayBenchReport report);
}

/// <summary>Reads guest memory in use. The host adapts the guest agent or hypervisor counters; the abstractions offer no such reading.</summary>
public interface IDisplayBenchMemory
{
    /// <summary>Memory in use inside the guest of <paramref name="target"/> right now, in bytes.</summary>
    Task<long> ReadGuestMemoryInUseBytesAsync(DisplayTarget target, CancellationToken ct);
}

/// <summary>Tells the benchmark which VMs to measure. A null target means that role is not set up on this host.</summary>
public interface IDisplayBenchTargets
{
    /// <summary>The VM running the kiosk profile with no browser.</summary>
    Task<DisplayTarget?> KioskAsync(CancellationToken ct);

    /// <summary>The VM that hosts browser sessions (the pool VM), whose memory the browser scenario reads.</summary>
    Task<DisplayTarget?> BrowserHostAsync(CancellationToken ct);

    /// <summary>The target to capture from and send input to through <paramref name="source"/>; for the browser source it carries a browser session id.</summary>
    Task<DisplayTarget?> ForSourceAsync(DisplaySourceKind source, CancellationToken ct);
}
