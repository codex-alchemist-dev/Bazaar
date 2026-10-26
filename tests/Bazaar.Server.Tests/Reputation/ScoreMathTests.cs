using System.Text.Json;
using Bazaar.Server.Reputation;

namespace Bazaar.Server.Tests.Reputation;

public class ScoreMathTests
{
    private const double _tolerance = 1e-6;
    private const int _seed = 20251026;

    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static DateTimeOffset Now => new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> GoldenVectorNames() =>
        LoadGolden().Select(v => new object[] { v.Name });

    [Theory]
    [MemberData(nameof(GoldenVectorNames))]
    public void Golden_vector_matches(string name)
    {
        var vector = LoadGolden().Single(v => v.Name == name);

        var policy = ApplyOverrides(WeightPolicy.Default, vector.Policy);
        var evidence = vector.Evidence.Select(e => new Evidence(
            Enum.Parse<EvidenceKind>(e.Kind, ignoreCase: false),
            e.X,
            e.OccurredAt,
            e.Reliability ?? 1.0)).ToList();

        var result = ScoreMath.Compute(evidence, policy, vector.Now);

        AssertClose(vector.Expect.S, result.S, $"{name} S");
        AssertClose(vector.Expect.Lcb, result.Lcb, $"{name} Lcb");
        AssertClose(vector.Expect.NEff, result.NEff, $"{name} NEff");
    }

    [Fact]
    public void No_evidence_gives_half_with_no_weight()
    {
        var result = ScoreMath.Compute([], WeightPolicy.Default, Now);

        Assert.Equal(0.5, result.S);
        Assert.Equal(0.0, result.NEff);
    }

    [Fact]
    public void One_revert_now_gives_0_375_from_the_design_doc()
    {
        var result = ScoreMath.Compute(
            [new Evidence(EvidenceKind.Revert, 0, Now)],
            WeightPolicy.Default,
            Now);

        Assert.Equal(0.375, result.S, _tolerance);
    }

    [Fact]
    public void Order_is_irrelevant_for_shuffled_evidence()
    {
        var rng = new Random(_seed);
        var evidence = RandomEvidence(rng, 25);

        var expected = ScoreMath.Compute(evidence, WeightPolicy.Default, Now);
        for (var i = 0; i < 20; i++)
        {
            var shuffled = Shuffle(evidence, rng);
            Assert.Equal(expected, ScoreMath.Compute(shuffled, WeightPolicy.Default, Now));
        }
    }

    [Fact]
    public void Replaying_the_same_evidence_gives_identical_results()
    {
        var evidence = RandomEvidence(new Random(_seed), 40);

        var first = ScoreMath.Compute(evidence, WeightPolicy.Default, Now);
        var second = ScoreMath.Compute(evidence.ToList(), WeightPolicy.Default, Now);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Appending_an_owner_accept_never_lowers_s()
    {
        var rng = new Random(_seed);
        for (var i = 0; i < 200; i++)
        {
            var evidence = RandomEvidence(rng, rng.Next(0, 15));
            var before = ScoreMath.Compute(evidence, WeightPolicy.Default, Now);

            var after = ScoreMath.Compute(
                evidence.Append(new Evidence(EvidenceKind.OwnerAccept, 1, Now)),
                WeightPolicy.Default,
                Now);

            Assert.True(after.S >= before.S - 1e-12, $"iteration {i}: S fell from {before.S} to {after.S}");
        }
    }

    [Fact]
    public void Appending_an_owner_reject_never_raises_s()
    {
        var rng = new Random(_seed);
        for (var i = 0; i < 200; i++)
        {
            var evidence = RandomEvidence(rng, rng.Next(0, 15));
            var before = ScoreMath.Compute(evidence, WeightPolicy.Default, Now);

            var after = ScoreMath.Compute(
                evidence.Append(new Evidence(EvidenceKind.OwnerReject, 0, Now)),
                WeightPolicy.Default,
                Now);

            Assert.True(after.S <= before.S + 1e-12, $"iteration {i}: S rose from {before.S} to {after.S}");
        }
    }

    [Fact]
    public void Old_good_outcome_weighs_less_than_a_new_one()
    {
        var old = ScoreMath.Compute(
            [new Evidence(EvidenceKind.OwnerAccept, 1, Now.AddDays(-360))],
            WeightPolicy.Default,
            Now);
        var recent = ScoreMath.Compute(
            [new Evidence(EvidenceKind.OwnerAccept, 1, Now)],
            WeightPolicy.Default,
            Now);

        Assert.True(old.S < recent.S);
        Assert.True(old.NEff < recent.NEff);
    }

    [Fact]
    public void Future_evidence_counts_as_age_zero()
    {
        var future = ScoreMath.Compute(
            [new Evidence(EvidenceKind.Revert, 0, Now.AddDays(5))],
            WeightPolicy.Default,
            Now);

        Assert.Equal(1.0, future.NEff, _tolerance);
    }

    [Fact]
    public void Result_carries_the_policy_version()
    {
        var policy = WeightPolicy.Default with { Version = 7 };

        var result = ScoreMath.Compute([], policy, Now);

        Assert.Equal(7, result.WeightsVersion);
    }

    [Fact]
    public void Evidence_outside_zero_to_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Evidence(EvidenceKind.OwnerAccept, 1.5, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Evidence(EvidenceKind.OwnerAccept, double.NaN, Now));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Evidence(EvidenceKind.PeerReview, 0.5, Now, reviewerReliability: -0.1));
    }

    [Fact]
    public void Negative_prior_is_rejected()
    {
        var policy = WeightPolicy.Default with { PriorK = -1 };

        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreMath.Compute([], policy, Now));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-180.0)]
    public void Non_positive_half_life_is_rejected(double halfLife)
    {
        var policy = WeightPolicy.Default with { HalfLifeDays = halfLife };

        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreMath.Compute([], policy, Now));
    }

    [Fact]
    public void Negative_weight_is_rejected()
    {
        var policy = WeightPolicy.Default with { CiFirstRun = -0.5 };

        Assert.Throws<ArgumentOutOfRangeException>(() => ScoreMath.Compute([], policy, Now));
    }

    private static List<Evidence> RandomEvidence(Random rng, int count)
    {
        var kinds = Enum.GetValues<EvidenceKind>();
        var items = new List<Evidence>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(new Evidence(
                kinds[rng.Next(kinds.Length)],
                rng.NextDouble(),
                Now.AddHours(-rng.Next(0, 24 * 800)),
                rng.NextDouble()));
        }

        return items;
    }

    private static List<T> Shuffle<T>(IReadOnlyList<T> items, Random rng)
    {
        var copy = items.ToList();
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy;
    }

    private static WeightPolicy ApplyOverrides(WeightPolicy basePolicy, GoldenPolicy? overrides)
    {
        if (overrides is null)
        {
            return basePolicy;
        }

        return basePolicy with
        {
            OwnerOutcome = overrides.OwnerOutcome ?? basePolicy.OwnerOutcome,
            Revert = overrides.Revert ?? basePolicy.Revert,
            PostMergeDefect = overrides.PostMergeDefect ?? basePolicy.PostMergeDefect,
            CiFirstRun = overrides.CiFirstRun ?? basePolicy.CiFirstRun,
            PeerReview = overrides.PeerReview ?? basePolicy.PeerReview,
            PriorK = overrides.PriorK ?? basePolicy.PriorK,
            HalfLifeDays = overrides.HalfLifeDays ?? basePolicy.HalfLifeDays,
        };
    }

    private static void AssertClose(double expected, double actual, string label)
    {
        Assert.True(
            Math.Abs(expected - actual) <= _tolerance,
            $"{label}: expected {expected:R}, actual {actual:R}");
    }

    private static List<GoldenVector> LoadGolden()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Reputation", "Golden", "score_math.json");
        var json = File.ReadAllText(path);

        return JsonSerializer.Deserialize<List<GoldenVector>>(json, _jsonOptions)
            ?? throw new InvalidOperationException("score_math.json is empty.");
    }

    public sealed class GoldenVector
    {
        public string Name { get; set; } = "";

        public DateTimeOffset Now { get; set; }

        public List<GoldenEvidence> Evidence { get; set; } = [];

        public GoldenPolicy? Policy { get; set; }

        public GoldenExpect Expect { get; set; } = new();
    }

    public sealed class GoldenEvidence
    {
        public string Kind { get; set; } = "";

        public double X { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public double? Reliability { get; set; }
    }

    public sealed class GoldenPolicy
    {
        public double? OwnerOutcome { get; set; }

        public double? Revert { get; set; }

        public double? PostMergeDefect { get; set; }

        public double? CiFirstRun { get; set; }

        public double? PeerReview { get; set; }

        public double? PriorK { get; set; }

        public double? HalfLifeDays { get; set; }
    }

    public sealed class GoldenExpect
    {
        public double S { get; set; }

        public double Lcb { get; set; }

        public double NEff { get; set; }
    }
}
