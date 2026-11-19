namespace Bazaar.Client.LocalInference.Speech.Eval;

/// <summary>
/// Outcome of one case. <c>Shown</c> is the text the user would see: the cleaned text, or the raw transcript when cleanup
/// fell back. <c>Passed</c> requires filler removal, kept facts and no added facts, and no fallback.
/// </summary>
public sealed record CleanerEvalResult(
    string Id,
    string Shown,
    bool FellBack,
    bool FillerRemoved,
    bool FactsKept,
    bool NoAddedFacts,
    bool ExactMatch,
    bool Passed);

/// <summary>Per-case results plus aggregate rates. Scores are fractions in [0, 1]; an empty run scores 0.</summary>
public sealed record CleanerEvalReport(IReadOnlyList<CleanerEvalResult> Cases)
{
    public int Total => Cases.Count;

    public int Passed => Cases.Count(c => c.Passed);

    /// <summary>Fraction of cases that passed. This is the aggregate score.</summary>
    public double Score => Rate(c => c.Passed);

    public double FillerRemovalRate => Rate(c => c.FillerRemoved);

    public double FactsKeptRate => Rate(c => c.FactsKept);

    public double NoAddedFactsRate => Rate(c => c.NoAddedFacts);

    public double ExactMatchRate => Rate(c => c.ExactMatch);

    public int FellBackCount => Cases.Count(c => c.FellBack);

    private double Rate(Func<CleanerEvalResult, bool> check) => Total == 0 ? 0 : Cases.Count(check) / (double)Total;
}
