namespace Bazaar.Kodo.Prediction;

/// <summary>One observation of a numeric series: a time and a value.</summary>
public readonly record struct TrendSample(DateTimeOffset At, double Value);

/// <summary>The outcome of offering a sample to a window or learner. Rejected samples change nothing.</summary>
public enum SampleAcceptance
{
    Accepted,

    /// <summary>NaN, infinity, or a value or time outside the accepted range.</summary>
    Invalid,

    /// <summary>The sample is not strictly newer than the previous one.</summary>
    OutOfOrder,
}

public static class SampleAcceptanceExtensions
{
    /// <summary>The cause string for a rejection, or null when the sample was accepted.</summary>
    public static string? ToCause(this SampleAcceptance acceptance) => acceptance switch
    {
        SampleAcceptance.Accepted => null,
        SampleAcceptance.OutOfOrder => PredictionCauses.OutOfOrderSample,
        _ => PredictionCauses.InvalidSample,
    };
}
