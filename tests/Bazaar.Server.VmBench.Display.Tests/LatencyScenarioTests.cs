using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmBench.Display.Tests;

public sealed class LatencyScenarioTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (FakeTimeProvider Time, FakeDisplay Display, FakeTargets Targets) Rig()
    {
        var time = new FakeTimeProvider();
        return (time, new FakeDisplay(time), new FakeTargets());
    }

    [Fact]
    public async Task Screenshot_records_the_distribution_per_source_through_the_clock()
    {
        var (time, display, targets) = Rig();
        display.LatencyMs[DisplaySourceKind.Hypervisor] = [10, 20, 30, 40, 50];
        display.LatencyMs[DisplaySourceKind.Guest] = [100];
        display.LatencyMs[DisplaySourceKind.Browser] = [200];

        var results = await new ScreenshotLatencyScenario(display, targets).RunAsync(Bench.Context(time), Ct);

        Assert.Equal(["hypervisor", "guest", "browser"], results.Select(r => r.Variant));
        var hyp = results[0];
        Assert.Equal(DisplayBenchOutcome.Passed, hyp.Outcome);
        Assert.Equal(5, hyp.Stat!.Count);
        Assert.Equal(10, hyp.Stat.Min, 3);
        Assert.Equal(30, hyp.Stat.Median, 3);
        Assert.Equal(50, hyp.Stat.P95, 3);
        Assert.Equal(50, hyp.Stat.Max, 3);
        Assert.Equal(30, hyp.Stat.Mean, 3);
        Assert.Equal(50, hyp.Value!.Value, 3);
        Assert.Equal(500, hyp.Threshold);
        Assert.Contains(hyp.Metrics, m => m.Name == "frameBytesMax" && m.Value == FakeDisplay.Pixels.Length);
        Assert.Contains(hyp.Metrics, m => m.Name == "frameWidthMedian" && m.Value == 1280);
        Assert.All(display.Captures, c => Assert.NotNull(c.Source));
        Assert.Equal(15, display.Captures.Count);
    }

    [Theory]
    [InlineData(500, DisplayBenchOutcome.Passed)]
    [InlineData(501, DisplayBenchOutcome.Failed)]
    public async Task Screenshot_p95_verdict_at_the_edge(double ms, DisplayBenchOutcome expected)
    {
        var (time, display, targets) = Rig();
        display.LatencyMs[DisplaySourceKind.Hypervisor] = [ms];

        var results = await new ScreenshotLatencyScenario(display, targets).RunAsync(Bench.Context(time), Ct);

        Assert.Equal(expected, results[0].Outcome);
    }

    [Theory]
    [InlineData(8 * 1024 * 1024, DisplayBenchOutcome.Passed)]
    [InlineData(8 * 1024 * 1024 + 1, DisplayBenchOutcome.Failed)]
    public async Task Screenshot_frame_size_limit_at_the_edge(int bytes, DisplayBenchOutcome expected)
    {
        var (time, display, targets) = Rig();
        display.FrameBytes = bytes;

        var results = await new ScreenshotLatencyScenario(display, targets).RunAsync(Bench.Context(time, Bench.Criteria(c => c.ScreenshotSamples = 1)), Ct);

        Assert.Equal(expected, results[0].Outcome);
        Assert.Contains(results[0].Metrics, m => m.Name == "frameBytesMax" && m.Value == bytes);
    }

    [Fact]
    public async Task Screenshot_source_that_is_missing_is_not_run_and_never_passes()
    {
        var (time, display, targets) = Rig();
        display.Available.Remove(DisplaySourceKind.Guest);
        targets.Sources[DisplaySourceKind.Browser] = null;

        var results = await new ScreenshotLatencyScenario(display, targets).RunAsync(Bench.Context(time), Ct);

        Assert.Equal(DisplayBenchOutcome.Passed, results[0].Outcome);
        Assert.Equal(DisplayBenchOutcome.NotRun, results[1].Outcome);
        Assert.Contains("not available", results[1].Reason, StringComparison.Ordinal);
        Assert.Equal(DisplayBenchOutcome.NotRun, results[2].Outcome);
        Assert.Null(results[1].Value);
        Assert.DoesNotContain(display.Captures, c => c.Source == DisplaySourceKind.Guest);
    }

    [Fact]
    public async Task Both_latency_scenarios_are_not_run_without_a_display_port()
    {
        var time = new FakeTimeProvider();

        var shots = await new ScreenshotLatencyScenario().RunAsync(Bench.Context(time), Ct);
        var inputs = await new InputLatencyScenario(null, new FakeTargets()).RunAsync(Bench.Context(time), Ct);

        Assert.Equal(3, shots.Count);
        Assert.All(shots.Concat(inputs), r => Assert.Equal(DisplayBenchOutcome.NotRun, r.Outcome));
    }

    [Fact]
    public async Task Screenshot_errors_fail_the_source_and_count_the_failures()
    {
        var (time, display, targets) = Rig();
        display.FailWhen = (source, n) => source == DisplaySourceKind.Guest && n == 1 ? new DisplayException(DisplayFailure.SourceFailed, "guest tool broke") : null;

        var results = await new ScreenshotLatencyScenario(display, targets).RunAsync(Bench.Context(time), Ct);

        Assert.Equal(DisplayBenchOutcome.Passed, results[0].Outcome);
        var guest = results[1];
        Assert.Equal(DisplayBenchOutcome.Failed, guest.Outcome);
        Assert.Equal(1, guest.Stat!.Failures);
        Assert.Equal(4, guest.Stat.Count);
        Assert.Contains("guest tool broke", guest.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Screenshot_frame_from_another_source_is_a_failed_sample()
    {
        var (time, display, targets) = Rig();
        display.WrongSource = DisplaySourceKind.Browser;

        var results = await new ScreenshotLatencyScenario(display, targets).RunAsync(Bench.Context(time, Bench.Criteria(c => c.ScreenshotSamples = 2)), Ct);

        Assert.Equal(DisplayBenchOutcome.Failed, results[0].Outcome);
        Assert.Equal(2, results[0].Stat!.Failures);
        Assert.Null(results[0].Value);
        Assert.Equal(DisplayBenchOutcome.Passed, results[2].Outcome);
    }

    [Fact]
    public async Task Screenshot_honours_cancellation()
    {
        var (time, display, targets) = Rig();
        using var cts = new CancellationTokenSource();
        display.FailWhen = (_, _) =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScreenshotLatencyScenario(display, targets).RunAsync(Bench.Context(time), cts.Token));
    }

    [Fact]
    public async Task Input_times_the_acknowledgement_per_source_after_an_untimed_warmup_capture()
    {
        var (time, display, targets) = Rig();
        display.LatencyMs[DisplaySourceKind.Hypervisor] = [5];
        display.LatencyMs[DisplaySourceKind.Guest] = [1500];
        display.LatencyMs[DisplaySourceKind.Browser] = [251];

        var results = await new InputLatencyScenario(display, targets).RunAsync(Bench.Context(time), Ct);

        Assert.Equal(DisplayBenchOutcome.Passed, results[0].Outcome);
        Assert.Equal(5, results[0].Value!.Value, 3);
        Assert.Equal(DisplayBenchOutcome.Passed, results[1].Outcome);
        Assert.Equal(1500, results[1].Threshold);
        Assert.Equal(DisplayBenchOutcome.Failed, results[2].Outcome);
        Assert.Equal(250, results[2].Threshold);
        Assert.Equal(15, display.Inputs.Count);
        Assert.Equal(3, display.Captures.Count);
        Assert.All(display.Inputs, i => Assert.IsType<PointerMove>(Assert.Single(i.Actions)));
        Assert.Equal(DisplaySourceKind.Guest, display.Inputs[5].Source);
    }

    [Fact]
    public async Task Input_errors_and_missing_sources_are_never_passes()
    {
        var (time, display, targets) = Rig();
        display.Available.Remove(DisplaySourceKind.Hypervisor);
        display.FailWhen = (source, n) => source == DisplaySourceKind.Browser && n >= 1 ? new DisplayException(DisplayFailure.Refused, "gate said no") : null;

        var results = await new InputLatencyScenario(display, targets).RunAsync(Bench.Context(time), Ct);

        Assert.Equal(DisplayBenchOutcome.NotRun, results[0].Outcome);
        Assert.Equal(DisplayBenchOutcome.Passed, results[1].Outcome);
        Assert.Equal(DisplayBenchOutcome.Failed, results[2].Outcome);
        Assert.Contains("gate said no", results[2].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Input_honours_cancellation()
    {
        var (time, display, targets) = Rig();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InputLatencyScenario(display, targets).RunAsync(Bench.Context(time), cts.Token));
    }
}
