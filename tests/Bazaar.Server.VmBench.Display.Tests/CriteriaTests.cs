using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.VmBench.Display.Tests;

public sealed class CriteriaTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.Empty(DisplayBenchCriteriaValidator.Check(new DisplayBenchCriteria()));
        Assert.True(new DisplayBenchCriteriaValidator().Validate(null, new DisplayBenchCriteria()).Succeeded);
    }

    public static TheoryData<string> InvalidCases() =>
    [
        nameof(DisplayBenchCriteria.SettleSeconds),
        nameof(DisplayBenchCriteria.DesktopIdleMaxMiB),
        nameof(DisplayBenchCriteria.BrowserHeadlessMaxDeltaMiB),
        nameof(DisplayBenchCriteria.BrowserHeadedMaxDeltaMiB),
        nameof(DisplayBenchCriteria.ScreenshotSamples),
        nameof(DisplayBenchCriteria.ScreenshotGuestP95MaxMs),
        nameof(DisplayBenchCriteria.ScreenshotMaxFrameBytes),
        nameof(DisplayBenchCriteria.InputSamples),
        nameof(DisplayBenchCriteria.InputBrowserP95MaxMs),
        nameof(DisplayBenchCriteria.DensityMinPairs),
        nameof(DisplayBenchCriteria.DensityMaxPairs),
        nameof(DisplayBenchCriteria.DensityWindowsVmMemoryMb),
        nameof(DisplayBenchCriteria.DensityLinuxVmMemoryMb),
        nameof(DisplayBenchCriteria.DensityQueueDetectSeconds),
    ];

    [Theory]
    [MemberData(nameof(InvalidCases))]
    public void Out_of_range_values_are_rejected_by_name(string property)
    {
        var c = new DisplayBenchCriteria();
        switch (property)
        {
            case nameof(DisplayBenchCriteria.ScreenshotMaxFrameBytes): c.ScreenshotMaxFrameBytes = 0; break;
            case nameof(DisplayBenchCriteria.DensityMaxPairs): c.DensityMaxPairs = c.DensityMinPairs - 1; break;
            case nameof(DisplayBenchCriteria.SettleSeconds): c.SettleSeconds = -1; break;
            case nameof(DisplayBenchCriteria.ScreenshotSamples): c.ScreenshotSamples = 0; break;
            case nameof(DisplayBenchCriteria.InputSamples): c.InputSamples = 1001; break;
            case nameof(DisplayBenchCriteria.DensityMinPairs): c.DensityMinPairs = 0; break;
            case nameof(DisplayBenchCriteria.DensityWindowsVmMemoryMb): c.DensityWindowsVmMemoryMb = 1; break;
            case nameof(DisplayBenchCriteria.DensityLinuxVmMemoryMb): c.DensityLinuxVmMemoryMb = 1; break;
            case nameof(DisplayBenchCriteria.DensityQueueDetectSeconds): c.DensityQueueDetectSeconds = 0; break;
            case nameof(DisplayBenchCriteria.InputBrowserP95MaxMs): c.InputBrowserP95MaxMs = double.NaN; break;
            case nameof(DisplayBenchCriteria.ScreenshotGuestP95MaxMs): c.ScreenshotGuestP95MaxMs = double.PositiveInfinity; break;
            default: typeof(DisplayBenchCriteria).GetProperty(property)!.SetValue(c, 0d); break;
        }

        var errors = DisplayBenchCriteriaValidator.Check(c);

        Assert.Contains(errors, e => e.Contains(property, StringComparison.Ordinal));
        Assert.False(new DisplayBenchCriteriaValidator().Validate(null, c).Succeeded);
    }

    [Fact]
    public void Every_default_is_documented_as_provisional()
    {
        var xml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Bazaar.Server.VmBench.Display", "DisplayBenchCriteria.cs"));
        var criteria = xml[..xml.IndexOf("public sealed record DisplayBenchCriteriaSnapshot", StringComparison.Ordinal)];
        var props = typeof(DisplayBenchCriteria).GetProperties().Select(p => p.Name).ToList();

        Assert.All(props, p =>
        {
            var at = criteria.IndexOf(" " + p + " {", StringComparison.Ordinal);
            Assert.True(at > 0, p);
            Assert.Contains("Provisional", criteria[Math.Max(0, at - 260)..at], StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Invalid_criteria_stop_the_run_before_any_scenario()
    {
        var time = new FakeTimeProvider();
        var scenario = new RecordingScenario(DisplayBenchScenario.DesktopIdleRam);
        var runner = Bench.Runner(time, Bench.Criteria(c => c.ScreenshotSamples = 0), scenario);

        await Assert.ThrowsAsync<OptionsValidationException>(() => runner.RunAsync("h", null, TestContext.Current.CancellationToken));

        Assert.Equal(0, scenario.Runs);
    }

    [Fact]
    public async Task Criteria_cannot_change_after_the_run_has_started()
    {
        var time = new FakeTimeProvider();
        var live = Bench.Criteria(c => c.DesktopIdleMaxMiB = 100);
        var first = new RecordingScenario(DisplayBenchScenario.DesktopIdleRam, () =>
        {
            live.DesktopIdleMaxMiB = 99999;
            live.ScreenshotSamples = 1;
        });
        var second = new RecordingScenario(DisplayBenchScenario.BrowserRam);
        var runner = Bench.Runner(time, live, first, second);

        var report = await runner.RunAsync("h", null, TestContext.Current.CancellationToken);

        Assert.Equal(100, report.Criteria.DesktopIdleMaxMiB);
        Assert.Equal(5, report.Criteria.ScreenshotSamples);
        Assert.Equal(100, second.SeenCriteria!.DesktopIdleMaxMiB);
        Assert.Equal(5, second.SeenCriteria.ScreenshotSamples);
    }

    [Fact]
    public void Snapshot_is_a_copy_with_every_value()
    {
        var c = new DisplayBenchCriteria { DensityMinPairs = 3, ScreenshotMaxFrameBytes = 77 };
        var snap = DisplayBenchCriteriaSnapshot.From(c);
        c.DensityMinPairs = 9;

        Assert.Equal(3, snap.DensityMinPairs);
        Assert.Equal(77, snap.ScreenshotMaxFrameBytes);
        foreach (var p in typeof(DisplayBenchCriteria).GetProperties())
        {
            Assert.NotNull(typeof(DisplayBenchCriteriaSnapshot).GetProperty(p.Name));
        }
    }
}

internal sealed class RecordingScenario : IDisplayBenchScenario
{
    private readonly Action? _during;

    public RecordingScenario(DisplayBenchScenario id, Action? during = null)
    {
        Id = id;
        _during = during;
    }

    public DisplayBenchScenario Id { get; }
    public int Runs { get; private set; }
    public DisplayBenchCriteriaSnapshot? SeenCriteria { get; private set; }
    public Func<Task<IReadOnlyList<DisplayBenchResult>>>? Override { get; set; }

    public Task<IReadOnlyList<DisplayBenchResult>> RunAsync(DisplayBenchContext context, CancellationToken ct)
    {
        Runs++;
        SeenCriteria = context.Criteria;
        _during?.Invoke();
        if (Override is not null)
        {
            return Override();
        }

        IReadOnlyList<DisplayBenchResult> r = [new DisplayBenchResult(Id, "v", DisplayBenchOutcome.Passed, 1, "u", 2, null, [], null)];
        return Task.FromResult(r);
    }
}
