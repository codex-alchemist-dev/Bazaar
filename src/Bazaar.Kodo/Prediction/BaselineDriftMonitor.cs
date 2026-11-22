namespace Bazaar.Kodo.Prediction;

/// <summary>
/// One series' baseline learner plus its drift detector. Each sample is first compared with the baseline learned from
/// earlier weeks, then added to it. While a bucket is warming up the answer is Unknown with
/// <see cref="PredictionCauses.BaselineWarmingUp"/>. Bad samples are rejected and change nothing. Advisory only.
/// </summary>
public sealed class BaselineDriftMonitor
{
    private readonly BaselineLearner _learner;
    private readonly DriftDetector _detector;

    public BaselineDriftMonitor(BaselineOptions options)
        : this(new BaselineLearner(options), new DriftDetector(options))
    {
    }

    private BaselineDriftMonitor(BaselineLearner learner, DriftDetector detector)
    {
        _learner = learner;
        _detector = detector;
    }

    public BaselineLearner Learner => _learner;

    public DriftAssessment Observe(DateTimeOffset at, double value)
    {
        SampleAcceptance acceptance = _learner.Check(at, value);
        if (acceptance != SampleAcceptance.Accepted)
        {
            return DriftAssessment.Unknown(acceptance.ToCause()!);
        }

        BaselineReading reading = _learner.Read(at);
        _learner.TryAdd(at, value);
        if (!reading.WarmedUp)
        {
            return DriftAssessment.Unknown(PredictionCauses.BaselineWarmingUp);
        }

        return _detector.Update(at, (value - reading.Median) / reading.Scale);
    }

    public static bool TryRestore(
        BaselineMemory? baseline, DriftMemory? drift, BaselineOptions options, out BaselineDriftMonitor? monitor)
    {
        monitor = null;
        if (!BaselineLearner.TryRestore(baseline, options, out BaselineLearner? learner)
            || !DriftDetector.TryRestore(drift, options, out DriftDetector? detector))
        {
            return false;
        }

        monitor = new BaselineDriftMonitor(learner!, detector!);
        return true;
    }

    public BaselineMemory ToBaselineMemory() => _learner.ToMemory();

    public DriftMemory ToDriftMemory() => _detector.ToMemory();
}
