using Bazaar.Kodo.Evaluator;

namespace Bazaar.Kodo.Tests.Evaluator;

/// <summary>
/// The 0 to 100 score: weighted mean and the caps of doc 46 section 12.2.
/// </summary>
public sealed class KodoScoreTests
{
    private static Dictionary<KodoCategory, int> AllCategories(int score)
    {
        Dictionary<KodoCategory, int> scores = [];
        foreach (KodoCategory category in Enum.GetValues<KodoCategory>())
        {
            scores[category] = score;
        }

        return scores;
    }

    [Fact]
    public void DefaultWeightsMatchSection12And2AndSumTo100()
    {
        // Section 12.2: server 25, data 20, security 15, modules 15, host 10, network 5, fleet 5, models 5.
        Assert.Equal(25, KodoScore.DefaultWeights[KodoCategory.Server]);
        Assert.Equal(20, KodoScore.DefaultWeights[KodoCategory.Data]);
        Assert.Equal(15, KodoScore.DefaultWeights[KodoCategory.Security]);
        Assert.Equal(15, KodoScore.DefaultWeights[KodoCategory.Modules]);
        Assert.Equal(10, KodoScore.DefaultWeights[KodoCategory.Host]);
        Assert.Equal(5, KodoScore.DefaultWeights[KodoCategory.Network]);
        Assert.Equal(5, KodoScore.DefaultWeights[KodoCategory.Fleet]);
        Assert.Equal(5, KodoScore.DefaultWeights[KodoCategory.Models]);
        Assert.Equal(100, KodoScore.DefaultWeights.Values.Sum());
    }

    [Fact]
    public void ScoreIsTheWeightedMeanOfTheCategories()
    {
        // Section 12.2: weighted mean. Server 100 at weight 25 and the other 75 weight points at 80 give 85.
        Dictionary<KodoCategory, int> scores = AllCategories(80);
        scores[KodoCategory.Server] = 100;

        int score = KodoScore.Compute(scores, new KodoObservation());

        Assert.Equal(85, score);
    }

    [Fact]
    public void WeightedMeanRoundsHalfUp()
    {
        // Weights 1 and 1 with scores 50 and 51 give 50.5, which rounds to 51.
        Dictionary<KodoCategory, int> weights = AllCategories(0);
        weights[KodoCategory.Server] = 1;
        weights[KodoCategory.Data] = 1;
        Dictionary<KodoCategory, int> scores = AllCategories(0);
        scores[KodoCategory.Server] = 50;
        scores[KodoCategory.Data] = 51;

        int score = KodoScore.Compute(scores, new KodoObservation(), weights);

        Assert.Equal(51, score);
    }

    [Theory]
    // Section 12.2: green, no caps apply, the mean is kept.
    [InlineData(false, false, false, 100, 100)]
    // Section 12.2: a failing critical probe caps the overall score at 49.
    [InlineData(true, false, false, 100, 49)]
    // Section 12.2: an unknown critical probe caps the overall score at 70.
    [InlineData(false, true, false, 100, 70)]
    // Section 12.2: an integrity failure caps the overall score at 10.
    [InlineData(false, false, true, 100, 10)]
    // Section 12.2: the lowest applicable cap wins, so integrity (10) beats critical red (49).
    [InlineData(true, false, true, 100, 10)]
    // Section 12.2: critical red and unknown together give the lower red cap, 49.
    [InlineData(true, true, false, 100, 49)]
    // Section 12.2: a cap never raises a score; a 30 with critical red stays 30.
    [InlineData(true, false, false, 30, 30)]
    // Section 12.2: a cap never raises a score; a 60 with unknown stays 60, not 70.
    [InlineData(false, true, false, 60, 60)]
    public void CapsApplyAsTheSafetyFloor(bool criticalRed, bool criticalUnknown, bool integrity, int categoryScore, int expected)
    {
        var observation = new KodoObservation
        {
            CriticalRed = criticalRed,
            CriticalUnknown = criticalUnknown,
            IntegrityOrContractFailure = integrity,
        };

        int score = KodoScore.Compute(AllCategories(categoryScore), observation);

        Assert.Equal(expected, score);
    }

    [Fact]
    public void CriticalRedDoesNotReadAsGreenWhateverTheCategories()
    {
        // Section 12.1 and 12.2: a red system cannot read as healthy because of its score.
        int score = KodoScore.Compute(AllCategories(100), new KodoObservation { CriticalRed = true });

        Assert.True(score <= KodoScore.CriticalRedCap);
    }

    [Fact]
    public void MissingCategoryIsRejected()
    {
        Dictionary<KodoCategory, int> scores = AllCategories(50);
        scores.Remove(KodoCategory.Models);

        Assert.Throws<ArgumentException>(() => KodoScore.Compute(scores, new KodoObservation()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void CategoryScoreOutsideZeroToHundredIsRejected(int score)
    {
        Dictionary<KodoCategory, int> scores = AllCategories(50);
        scores[KodoCategory.Host] = score;

        Assert.Throws<ArgumentOutOfRangeException>(() => KodoScore.Compute(scores, new KodoObservation()));
    }

    [Fact]
    public void ZeroTotalWeightIsRejected()
    {
        Dictionary<KodoCategory, int> weights = AllCategories(0);

        Assert.Throws<ArgumentException>(() => KodoScore.Compute(AllCategories(50), new KodoObservation(), weights));
    }
}
