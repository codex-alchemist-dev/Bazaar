namespace Bazaar.Client.Materials.Tests;

public class PolledMaterialEnvironmentSourceTests
{
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static readonly MaterialEnvironment _windows = new(MaterialPlatform.Windows, MicaAvailable: true);

    [Fact]
    public void The_probe_is_reused_until_it_is_older_than_the_max_age()
    {
        var time = new ManualTime();
        var reads = 0;
        var source = new PolledMaterialEnvironmentSource(() => { reads++; return _windows; }, time, TimeSpan.FromSeconds(2));

        _ = source.Current;
        _ = source.Current;
        Assert.Equal(1, reads);

        time.Advance(TimeSpan.FromSeconds(3));
        _ = source.Current;
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Invalidate_forces_a_fresh_read()
    {
        var reads = 0;
        var source = new PolledMaterialEnvironmentSource(() => { reads++; return _windows; }, new ManualTime());

        _ = source.Current;
        source.Invalidate();
        _ = source.Current;

        Assert.Equal(2, reads);
    }

    [Fact]
    public void Setting_frame_budget_and_backdrop_feedback_are_layered_over_the_probe()
    {
        var source = new PolledMaterialEnvironmentSource(() => _windows, new ManualTime())
        {
            Level = MaterialLevel.Translucent,
            FrameBudgetMissed = true,
        };

        source.ReportBackdropFailed();
        MaterialEnvironment env = source.Current;

        Assert.Equal(MaterialLevel.Translucent, env.Level);
        Assert.True(env.FrameBudgetMissed);
        Assert.True(env.BackdropRequestFailed);
        Assert.True(env.MicaAvailable);
        Assert.Equal(MaterialReason.BackdropFailed, MaterialPolicy.Resolve(MaterialSurface.Chrome, env).Reason);
    }
}
