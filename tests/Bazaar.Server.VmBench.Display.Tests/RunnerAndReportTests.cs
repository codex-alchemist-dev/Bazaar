using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmBench.Display.Tests;

public sealed class RunnerAndReportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DisplayBenchResult Result(DisplayBenchOutcome o) => new(DisplayBenchScenario.DesktopIdleRam, "v", o, 1, "u", 2, null, [], null);

    [Fact]
    public async Task Runs_every_registered_scenario_in_enum_order_or_only_the_selected_ones()
    {
        var time = new FakeTimeProvider();
        var a = new RecordingScenario(DisplayBenchScenario.DensityRamp);
        var b = new RecordingScenario(DisplayBenchScenario.DesktopIdleRam);
        var runner = Bench.Runner(time, Bench.Criteria(), a, b);

        var all = await runner.RunAsync("host", null, Ct);
        var some = await runner.RunAsync("host", [DisplayBenchScenario.DensityRamp], Ct);

        Assert.Equal([DisplayBenchScenario.DesktopIdleRam, DisplayBenchScenario.DensityRamp], all.Results.Select(r => r.Scenario));
        Assert.Equal([DisplayBenchScenario.DensityRamp], some.Results.Select(r => r.Scenario));
        Assert.Equal("host", all.HostLabel);
    }

    [Fact]
    public async Task A_scenario_that_throws_is_reported_failed()
    {
        var time = new FakeTimeProvider();
        var bad = new RecordingScenario(DisplayBenchScenario.BrowserRam) { Override = () => throw new InvalidOperationException("boom") };

        var report = await Bench.Runner(time, Bench.Criteria(), bad).RunAsync("h", null, Ct);

        var r = Assert.Single(report.Results);
        Assert.Equal(DisplayBenchOutcome.Failed, r.Outcome);
        Assert.Contains("boom", r.Reason, StringComparison.Ordinal);
        Assert.Equal(DisplayBenchOutcome.Failed, report.Overall);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var runner = Bench.Runner(new FakeTimeProvider(), Bench.Criteria(), new RecordingScenario(DisplayBenchScenario.BrowserRam));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("h", null, cts.Token));
    }

    [Fact]
    public async Task Timestamps_come_from_the_time_provider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero));
        var s = new RecordingScenario(DisplayBenchScenario.BrowserRam) { Override = () => { time.Advance(TimeSpan.FromSeconds(7)); return Task.FromResult<IReadOnlyList<DisplayBenchResult>>([]); } };

        var report = await Bench.Runner(time, Bench.Criteria(), s).RunAsync("h", null, Ct);

        Assert.Equal(new DateTimeOffset(2026, 11, 22, 8, 0, 0, TimeSpan.Zero), report.StartedAt);
        Assert.Equal(TimeSpan.FromSeconds(7), report.FinishedAt - report.StartedAt);
    }

    [Fact]
    public void Overall_is_a_pass_only_when_something_ran_and_everything_passed()
    {
        Assert.Equal(DisplayBenchOutcome.NotRun, DisplayBenchRunner.Overall([]));
        Assert.Equal(DisplayBenchOutcome.Passed, DisplayBenchRunner.Overall([Result(DisplayBenchOutcome.Passed)]));
        Assert.Equal(DisplayBenchOutcome.NotRun, DisplayBenchRunner.Overall([Result(DisplayBenchOutcome.Passed), Result(DisplayBenchOutcome.NotRun)]));
        Assert.Equal(DisplayBenchOutcome.Failed, DisplayBenchRunner.Overall([Result(DisplayBenchOutcome.NotRun), Result(DisplayBenchOutcome.Failed)]));
    }

    [Fact]
    public async Task Report_carries_the_criteria_and_no_frame_bytes_or_hashes()
    {
        var time = new FakeTimeProvider();
        var display = new FakeDisplay(time);
        var targets = new FakeTargets();
        var criteria = Bench.Criteria(c => c.ScreenshotHypervisorP95MaxMs = 123.5);
        var runner = Bench.Runner(
            time,
            criteria,
            new ScreenshotLatencyScenario(display, targets),
            new InputLatencyScenario(display, targets),
            new DesktopIdleRamScenario(targets, new FakeMemory(512)),
            new DensityRampScenario());
        var report = await runner.RunAsync("owner-pc", null, Ct);
        var writer = new DisplayBenchReportWriter();

        var json = writer.ToJson(report);
        var md = writer.ToMarkdown(report);

        var parsed = JsonElement.Parse(json);
        Assert.Equal(123.5, parsed.GetProperty("criteria").GetProperty("screenshotHypervisorP95MaxMs").GetDouble());
        Assert.Equal("NotRun", parsed.GetProperty("overall").GetString());
        Assert.Contains("ScreenshotHypervisorP95MaxMs | 123.5 |", md, StringComparison.Ordinal);
        Assert.Contains("## Criteria (fixed at run start)", md, StringComparison.Ordinal);
        Assert.Contains("NotRun", md, StringComparison.Ordinal);
        Assert.Contains("provisional", md, StringComparison.Ordinal);
        Assert.True(md.IndexOf("## Criteria", StringComparison.Ordinal) < md.IndexOf("## Results", StringComparison.Ordinal));

        var sha = ScreenFrame.ComputeSha256(FakeDisplay.Pixels);
        foreach (var text in new[] { json, md })
        {
            Assert.DoesNotContain(Convert.ToBase64String(FakeDisplay.Pixels), text, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexStringLower(FakeDisplay.Pixels), text, StringComparison.Ordinal);
            Assert.DoesNotContain(sha, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Png", text, StringComparison.Ordinal);
        }

        Assert.Contains("frameBytesMax", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_for_an_empty_run_says_so_and_is_deterministic()
    {
        var report = new DisplayBenchReport("r", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "h", DisplayBenchCriteriaSnapshot.From(new DisplayBenchCriteria()), [], DisplayBenchOutcome.NotRun);
        var writer = new DisplayBenchReportWriter();

        Assert.Contains("No scenarios ran", writer.ToMarkdown(report), StringComparison.Ordinal);
        Assert.Equal(writer.ToMarkdown(report), writer.ToMarkdown(report));
        Assert.EndsWith(DisplayBenchReportWriter.ClosingLine + "\n", writer.ToMarkdown(report), StringComparison.Ordinal);
    }
}
