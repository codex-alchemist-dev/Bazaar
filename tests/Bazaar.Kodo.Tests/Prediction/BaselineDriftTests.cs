using Bazaar.Kodo.Prediction;
using Bazaar.Kodo.Probes;

namespace Bazaar.Kodo.Tests.Prediction;

public sealed class BaselineDriftTests
{
    private static readonly TimeSpan _hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan _week = TimeSpan.FromDays(7);

    private static DateTimeOffset At(int hours) => PredictionTestData.Start + (_hour * hours);

    [Fact]
    public void BucketIsWarmingUpUntilMinimumWeeksExist()
    {
        var monitor = new BaselineDriftMonitor(new BaselineOptions());

        // Same hour of the week, one sample per week. Weeks 0, 1, 2 have 0, 1, 2 earlier weeks: warming up.
        for (int week = 0; week < 3; week++)
        {
            DriftAssessment assessment = monitor.Observe(PredictionTestData.Start + (_week * week), 100);
            Assert.Equal(DriftState.Unknown, assessment.State);
            Assert.Equal(PredictionCauses.BaselineWarmingUp, assessment.Cause);
        }

        DriftAssessment warm = monitor.Observe(PredictionTestData.Start + (_week * 3), 100);
        Assert.Equal(DriftState.Stable, warm.State);
        Assert.Equal(ProbeStatus.Green, PredictionProbeMapper.ToProbeResult(warm).Status);
    }

    [Fact]
    public void WarmUpResultMapsToUnknownNeverGreen()
    {
        var probe = new BaselineDriftProbe("x", TimeProvider.System);

        ProbeResult result = probe.Observe(PredictionTestData.Start, 100);

        Assert.Equal(ProbeStatus.Unknown, result.Status);
        Assert.Equal(PredictionCauses.BaselineWarmingUp, result.Cause);
    }

    private static BaselineDriftMonitor WarmMonitor(out int nextHour, double value = 100)
    {
        var monitor = new BaselineDriftMonitor(new BaselineOptions());
        int hours = 24 * 7 * 4;
        for (int h = 0; h < hours; h++)
        {
            monitor.Observe(At(h), value);
        }

        nextHour = hours;
        return monitor;
    }

    [Fact]
    public void StableSeriesStaysGreenAfterWarmUp()
    {
        BaselineDriftMonitor monitor = WarmMonitor(out int next);

        for (int h = next; h < next + 48; h++)
        {
            Assert.Equal(DriftState.Stable, monitor.Observe(At(h), 100).State);
        }
    }

    [Fact]
    public void SustainedShiftUpRaisesDriftWithStartAndMagnitude()
    {
        BaselineDriftMonitor monitor = WarmMonitor(out int next);

        DriftAssessment first = monitor.Observe(At(next), 140);
        DriftAssessment second = monitor.Observe(At(next + 1), 140);

        Assert.Equal(DriftState.Stable, first.State); // one outlier alone never alarms
        Assert.Equal(DriftState.DriftUp, second.State);
        Assert.Equal(PredictionCauses.DriftUp, second.Cause);
        Assert.Equal(At(next), second.StartedAt);
        Assert.True(second.Magnitude > 5);
        ProbeResult probe = PredictionProbeMapper.ToProbeResult(second);
        Assert.Equal(ProbeStatus.Amber, probe.Status);
        Assert.Equal(PredictionCauses.DriftUp, probe.Cause);
    }

    [Fact]
    public void SustainedShiftDownRaisesDriftDown()
    {
        BaselineDriftMonitor monitor = WarmMonitor(out int next);

        monitor.Observe(At(next), 60);
        DriftAssessment second = monitor.Observe(At(next + 1), 60);

        Assert.Equal(DriftState.DriftDown, second.State);
        Assert.Equal(ProbeStatus.Amber, PredictionProbeMapper.ToProbeResult(second).Status);
        Assert.Equal(PredictionCauses.DriftDown, second.Cause);
    }

    [Fact]
    public void ShiftWithinSlackNeverAlarms()
    {
        BaselineDriftMonitor monitor = WarmMonitor(out int next);

        // Scale is 5 (5 percent floor), slack is half a scale unit: +2.4 stays under the slack.
        for (int h = next; h < next + 200; h++)
        {
            Assert.Equal(DriftState.Stable, monitor.Observe(At(h), 102.4).State);
        }
    }

    [Fact]
    public void DriftClearsWhenTheSeriesReturnsToNormal()
    {
        BaselineDriftMonitor monitor = WarmMonitor(out int next);
        monitor.Observe(At(next), 140);
        Assert.Equal(DriftState.DriftUp, monitor.Observe(At(next + 1), 140).State);

        DriftState state = DriftState.DriftUp;
        for (int h = next + 2; h < next + 20 && state == DriftState.DriftUp; h++)
        {
            state = monitor.Observe(At(h), 100).State;
        }

        Assert.Equal(DriftState.Stable, state);
    }

    [Fact]
    public void RejectedSamplesAreUnknownAndLeaveStateUnchanged()
    {
        BaselineDriftMonitor monitor = WarmMonitor(out int next);
        string before = Convert.ToHexString(PredictionMemoryCodec.ToUtf8Bytes(Wrap(monitor)));

        Assert.Equal(PredictionCauses.InvalidSample, monitor.Observe(At(next), double.NaN).Cause);
        Assert.Equal(PredictionCauses.InvalidSample, monitor.Observe(At(next), double.PositiveInfinity).Cause);
        Assert.Equal(PredictionCauses.InvalidSample, monitor.Observe(At(next), 1e300).Cause);
        Assert.Equal(PredictionCauses.InvalidSample, monitor.Observe(DateTimeOffset.UnixEpoch - _hour, 1).Cause);
        Assert.Equal(PredictionCauses.OutOfOrderSample, monitor.Observe(At(next - 5), 100).Cause);
        Assert.Equal(PredictionCauses.OutOfOrderSample, monitor.Observe(At(next - 1), 100).Cause);

        Assert.Equal(before, Convert.ToHexString(PredictionMemoryCodec.ToUtf8Bytes(Wrap(monitor))));
    }

    [Fact]
    public void MemoryIsBoundedOverManyWeeks()
    {
        var options = new BaselineOptions();
        var learner = new BaselineLearner(options);

        for (int h = 0; h < 24 * 7 * 20; h++)
        {
            learner.TryAdd(At(h), h % 17);
        }

        Assert.True(learner.StoredCount <= BaselineOptions.BucketCount * (options.TrailingWeeks + 1));
    }

    [Fact]
    public void ScaleFloorKeepsAFlatSeriesFromProducingHugeResiduals()
    {
        var learner = new BaselineLearner(new BaselineOptions());
        for (int week = 0; week < 4; week++)
        {
            learner.TryAdd(PredictionTestData.Start + (_week * week), 0);
        }

        BaselineReading reading = learner.Read(PredictionTestData.Start + (_week * 4));

        Assert.True(reading.WarmedUp);
        Assert.Equal(0, reading.Median);
        Assert.Equal(1.0, reading.Scale);
        Assert.Equal(4, reading.Weeks);
    }

    [Fact]
    public void MedianAndMadIgnoreAnOutlierWeek()
    {
        var learner = new BaselineLearner(new BaselineOptions { AbsoluteScaleFloor = 0.001, RelativeScaleFloor = 0 });
        double[] values = [10, 11, 9, 1000];
        for (int week = 0; week < values.Length; week++)
        {
            learner.TryAdd(PredictionTestData.Start + (_week * week), values[week]);
        }

        BaselineReading reading = learner.Read(PredictionTestData.Start + (_week * 4));

        Assert.Equal(10.5, reading.Median);
        Assert.InRange(reading.Scale, 0.5, 2.5);
    }

    [Fact]
    public void SameInputGivesSameAssessments()
    {
        static List<DriftAssessment> Run()
        {
            var monitor = new BaselineDriftMonitor(new BaselineOptions());
            var all = new List<DriftAssessment>();
            for (int h = 0; h < 24 * 7 * 5; h++)
            {
                all.Add(monitor.Observe(At(h), 100 + (h % 13) + (h > 24 * 7 * 4 + 5 ? 50 : 0)));
            }

            return all;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BaselineOptions { MinWeeks = 5 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BaselineOptions { TrailingWeeks = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BaselineOptions { CusumThreshold = double.NaN }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BaselineOptions { AbsoluteScaleFloor = 0 }.Validate());
    }

    private static PredictionMemory Wrap(BaselineDriftMonitor monitor) =>
        new(PredictionMemory.CurrentVersion, null, monitor.ToBaselineMemory(), monitor.ToDriftMemory());
}
