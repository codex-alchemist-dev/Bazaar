using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench.Tests;

public sealed class CodingBenchSummarizerTests
{
    private static CodingBenchSummary Summary(
        string model,
        BenchEditFormat format,
        int runs,
        double passRate,
        double compliance = 1.0,
        double fakeRate = 0.0,
        double tokens = 1000.0) =>
        new(model, format, runs, passRate, compliance, 1.0 - compliance, fakeRate, tokens, 5.0, null);

    [Fact]
    public void Summarize_computes_known_numbers_for_one_model_and_format()
    {
        var results = new[]
        {
            // Correct false, claimed and fake; 10 edits with 2 malformed; cost 0.10.
            TestData.Result("model-a", BenchEditFormat.WholeFile, correct: false,
                outcome: TestData.CleanOutcome() with { ClaimedSuccess = true, EditsAttempted = 10, EditsMalformed = 2, CostUsd = 0.10m, CostSource = "lane-api" },
                fakeSuccess: true),
            // Correct, claimed, 0 edits; input 400 + output 200; 3 turns; no cost.
            TestData.Result("model-a", BenchEditFormat.WholeFile, correct: true,
                TestData.CleanOutcome() with { ClaimedSuccess = true, InputTokens = 400, Turns = 3 }),
            // Correct, not claimed; 5 edits with 1 malformed; input 600 + output 200; 7 turns; cost 0.05.
            TestData.Result("model-a", BenchEditFormat.WholeFile, correct: true,
                TestData.CleanOutcome() with { EditsAttempted = 5, EditsMalformed = 1, InputTokens = 600, Turns = 7, CostUsd = 0.05m, CostSource = "lane-api" }),
            // Correct, claimed, 5 edits none malformed; default tokens 1200; 5 turns; no cost.
            TestData.Result("model-a", BenchEditFormat.WholeFile, correct: true,
                TestData.CleanOutcome() with { ClaimedSuccess = true, EditsAttempted = 5 }),
        };

        var summaries = CodingBenchSummarizer.Summarize(results);

        var s = Assert.Single(summaries);
        Assert.Equal("model-a", s.Model);
        Assert.Equal(BenchEditFormat.WholeFile, s.Format);
        Assert.Equal(4, s.Runs);
        Assert.Equal(0.75, s.PassRate, 6);
        Assert.Equal(0.85, s.FormatCompliance, 6);
        Assert.Equal(0.15, s.MalformedEditRate, 6);
        // One fake run out of three that claimed success.
        Assert.Equal(1.0 / 3.0, s.FakeSuccessRate, 6);
        // (1200 + 600 + 800 + 1200) / 4
        Assert.Equal(950.0, s.MeanTokensPerTask, 6);
        Assert.Equal(5.0, s.MeanTurns, 6);
        Assert.Equal(0.15m, s.TotalCostUsd);
    }

    [Fact]
    public void Summarize_uses_full_compliance_and_zero_rates_when_nothing_was_attempted_or_claimed()
    {
        var results = new[]
        {
            TestData.Result("model-a", BenchEditFormat.SearchReplace, correct: false),
            TestData.Result("model-a", BenchEditFormat.SearchReplace, correct: false),
        };

        var s = Assert.Single(CodingBenchSummarizer.Summarize(results));

        Assert.Equal(0.0, s.PassRate, 6);
        Assert.Equal(1.0, s.FormatCompliance, 6);
        Assert.Equal(0.0, s.MalformedEditRate, 6);
        Assert.Equal(0.0, s.FakeSuccessRate, 6);
        Assert.Null(s.TotalCostUsd);
    }

    [Fact]
    public void Total_cost_sums_only_non_null_costs()
    {
        var results = new[]
        {
            TestData.Result("model-a", BenchEditFormat.LineAnchored, correct: true,
                TestData.CleanOutcome() with { CostUsd = 0.25m, CostSource = "owner-api" }),
            TestData.Result("model-a", BenchEditFormat.LineAnchored, correct: true),
            TestData.Result("model-a", BenchEditFormat.LineAnchored, correct: true,
                TestData.CleanOutcome() with { CostUsd = 0.5m, CostSource = "owner-api" }),
        };

        var s = Assert.Single(CodingBenchSummarizer.Summarize(results));

        Assert.Equal(0.75m, s.TotalCostUsd);
    }

    [Fact]
    public void Summarize_orders_by_model_then_format_regardless_of_input_order()
    {
        var results = new[]
        {
            TestData.Result("model-b", BenchEditFormat.SearchReplace, correct: true),
            TestData.Result("model-a", BenchEditFormat.SearchReplace, correct: true),
            TestData.Result("model-b", BenchEditFormat.WholeFile, correct: true),
            TestData.Result("model-a", BenchEditFormat.WholeFile, correct: true),
            TestData.Result("model-a", BenchEditFormat.LineAnchored, correct: true),
        };

        var summaries = CodingBenchSummarizer.Summarize(results);

        Assert.Equal(
            new[]
            {
                ("model-a", BenchEditFormat.WholeFile),
                ("model-a", BenchEditFormat.LineAnchored),
                ("model-a", BenchEditFormat.SearchReplace),
                ("model-b", BenchEditFormat.WholeFile),
                ("model-b", BenchEditFormat.SearchReplace),
            },
            summaries.Select(s => (s.Model, s.Format)).ToArray());
    }

    [Fact]
    public void Summarize_returns_nothing_for_no_results()
    {
        Assert.Empty(CodingBenchSummarizer.Summarize([]));
    }

    [Fact]
    public void Recommend_picks_the_highest_pass_rate()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.WholeFile, runs: 4, passRate: 0.5),
            Summary("model-a", BenchEditFormat.LineAnchored, runs: 4, passRate: 0.75),
        };

        var recommended = CodingBenchSummarizer.RecommendFormat(summaries);

        Assert.Equal(BenchEditFormat.LineAnchored, recommended["model-a"]);
    }

    [Fact]
    public void Recommend_breaks_pass_rate_ties_by_higher_format_compliance()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.SearchReplace, runs: 4, passRate: 0.5, compliance: 0.8),
            Summary("model-a", BenchEditFormat.WholeFile, runs: 4, passRate: 0.5, compliance: 0.9),
        };

        Assert.Equal(BenchEditFormat.WholeFile, CodingBenchSummarizer.RecommendFormat(summaries)["model-a"]);
    }

    [Fact]
    public void Recommend_breaks_ties_by_lower_mean_tokens()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.WholeFile, runs: 4, passRate: 0.5, tokens: 1200),
            Summary("model-a", BenchEditFormat.LineAnchored, runs: 4, passRate: 0.5, tokens: 800),
        };

        Assert.Equal(BenchEditFormat.LineAnchored, CodingBenchSummarizer.RecommendFormat(summaries)["model-a"]);
    }

    [Fact]
    public void Recommend_breaks_ties_by_lower_fake_success_rate()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.WholeFile, runs: 4, passRate: 0.5, fakeRate: 0.1),
            Summary("model-a", BenchEditFormat.SearchReplace, runs: 4, passRate: 0.5, fakeRate: 0.0),
        };

        Assert.Equal(BenchEditFormat.SearchReplace, CodingBenchSummarizer.RecommendFormat(summaries)["model-a"]);
    }

    [Fact]
    public void Recommend_falls_back_to_enum_order_when_everything_ties()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.SearchReplace, runs: 3, passRate: 0.5),
            Summary("model-a", BenchEditFormat.LineAnchored, runs: 3, passRate: 0.5),
            Summary("model-a", BenchEditFormat.WholeFile, runs: 3, passRate: 0.5),
        };

        Assert.Equal(BenchEditFormat.WholeFile, CodingBenchSummarizer.RecommendFormat(summaries)["model-a"]);
    }

    [Fact]
    public void Recommend_ignores_formats_with_fewer_than_three_runs()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.WholeFile, runs: 3, passRate: 0.5),
            Summary("model-a", BenchEditFormat.SearchReplace, runs: 2, passRate: 1.0),
        };

        Assert.Equal(BenchEditFormat.WholeFile, CodingBenchSummarizer.RecommendFormat(summaries)["model-a"]);
    }

    [Fact]
    public void Recommend_counts_exactly_three_runs_as_qualifying()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.WholeFile, runs: 4, passRate: 0.5),
            Summary("model-a", BenchEditFormat.SearchReplace, runs: 3, passRate: 1.0),
        };

        Assert.Equal(BenchEditFormat.SearchReplace, CodingBenchSummarizer.RecommendFormat(summaries)["model-a"]);
    }

    [Fact]
    public void Recommend_maps_a_model_with_no_qualifying_format_to_whole_file()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.SearchReplace, runs: 2, passRate: 1.0),
            Summary("model-a", BenchEditFormat.LineAnchored, runs: 1, passRate: 1.0),
        };

        Assert.Equal(BenchEditFormat.WholeFile, CodingBenchSummarizer.RecommendFormat(summaries)["model-a"]);
    }

    [Fact]
    public void Recommend_gives_each_model_its_own_format()
    {
        var summaries = new[]
        {
            Summary("model-a", BenchEditFormat.WholeFile, runs: 3, passRate: 0.2),
            Summary("model-a", BenchEditFormat.SearchReplace, runs: 3, passRate: 0.9),
            Summary("model-b", BenchEditFormat.WholeFile, runs: 3, passRate: 0.9),
            Summary("model-b", BenchEditFormat.LineAnchored, runs: 3, passRate: 0.4),
        };

        var recommended = CodingBenchSummarizer.RecommendFormat(summaries);

        Assert.Equal(2, recommended.Count);
        Assert.Equal(BenchEditFormat.SearchReplace, recommended["model-a"]);
        Assert.Equal(BenchEditFormat.WholeFile, recommended["model-b"]);
    }

    [Fact]
    public void Recommend_returns_nothing_for_no_summaries()
    {
        Assert.Empty(CodingBenchSummarizer.RecommendFormat([]));
    }
}
