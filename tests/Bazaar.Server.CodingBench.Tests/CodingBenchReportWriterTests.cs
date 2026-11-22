using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench.Tests;

public sealed class CodingBenchReportWriterTests
{
    private static readonly DateTimeOffset _started = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _finished = new(2026, 11, 19, 12, 30, 0, TimeSpan.Zero);

    private static CodingBenchReport BuildReport()
    {
        var pass = TestData.Task(ExpectedOutcome.Pass, CodingTaskKind.BugFix, "bug-1");
        var impossible = TestData.Task(ExpectedOutcome.MustReportImpossible, CodingTaskKind.TrapImpossible, "trap-impossible");
        var wrongTest = TestData.Task(ExpectedOutcome.MustFlagWrongTest, CodingTaskKind.TrapWrongTest, "trap-wrong");

        var results = new[]
        {
            CodingRunScorer.Score(TestData.Request("model-a", BenchEditFormat.WholeFile, pass),
                TestData.CleanOutcome() with { ClaimedSuccess = true }, verifierPassed: true, TimeSpan.FromSeconds(10)),
            CodingRunScorer.Score(TestData.Request("model-a", BenchEditFormat.WholeFile, pass),
                TestData.CleanOutcome() with { ClaimedSuccess = true }, verifierPassed: false, TimeSpan.FromSeconds(11)),
            CodingRunScorer.Score(TestData.Request("model-a", BenchEditFormat.WholeFile, impossible),
                TestData.CleanOutcome() with { ReportedImpossible = true }, verifierPassed: false, TimeSpan.FromSeconds(12)),
            CodingRunScorer.Score(TestData.Request("model-b", BenchEditFormat.SearchReplace, pass),
                TestData.CleanOutcome() with { CostUsd = 0.25m, CostSource = "owner-api" }, verifierPassed: true, TimeSpan.FromSeconds(13)),
            CodingRunScorer.Score(TestData.Request("model-b", BenchEditFormat.SearchReplace, wrongTest),
                TestData.CleanOutcome() with { ClaimedSuccess = true, CostUsd = 0.25m, CostSource = "owner-api" }, verifierPassed: true, TimeSpan.FromSeconds(14)),
        };

        var summaries = CodingBenchSummarizer.Summarize(results);
        return new CodingBenchReport(
            RunId: "run-2026-11-19",
            StartedAt: _started,
            FinishedAt: _finished,
            Results: results,
            Summaries: summaries,
            RecommendedFormat: CodingBenchSummarizer.RecommendFormat(summaries));
    }

    [Fact]
    public void Markdown_has_header_with_run_id_and_times()
    {
        var markdown = new CodingBenchReportWriter().ToMarkdown(BuildReport());

        Assert.StartsWith("# Coding benchmark report", markdown, StringComparison.Ordinal);
        Assert.Contains("- Run: `run-2026-11-19`", markdown, StringComparison.Ordinal);
        Assert.Contains("- Started: 2026-11-19 10:00:00 +00:00", markdown, StringComparison.Ordinal);
        Assert.Contains("- Finished: 2026-11-19 12:30:00 +00:00", markdown, StringComparison.Ordinal);
        Assert.Contains("- Runs: 5", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_summary_row_shows_free_lane_cost_when_no_cost_was_recorded()
    {
        var markdown = new CodingBenchReportWriter().ToMarkdown(BuildReport());

        // model-a WholeFile: 3 runs, 2 correct (66.7%), no costs.
        Assert.Contains("| model-a | WholeFile | 3 | 66.7% |", markdown, StringComparison.Ordinal);
        Assert.Contains("n/a (free lane)", markdown, StringComparison.Ordinal);
        Assert.Contains("Cost sources are recorded per run in the JSON report", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_summary_row_shows_total_cost_when_recorded()
    {
        var markdown = new CodingBenchReportWriter().ToMarkdown(BuildReport());

        Assert.Contains("| model-b | SearchReplace | 2 | 50.0% |", markdown, StringComparison.Ordinal);
        Assert.Contains("$0.5000", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_lists_recommended_format_per_model_in_order()
    {
        var markdown = new CodingBenchReportWriter().ToMarkdown(BuildReport());

        var modelA = markdown.IndexOf("- model-a: WholeFile", StringComparison.Ordinal);
        var modelB = markdown.IndexOf("- model-b: WholeFile", StringComparison.Ordinal);
        Assert.True(modelA >= 0);
        Assert.True(modelB > modelA);
    }

    [Fact]
    public void Markdown_traps_section_counts_correct_runs_per_trap_kind()
    {
        var markdown = new CodingBenchReportWriter().ToMarkdown(BuildReport());

        Assert.Contains("## Traps", markdown, StringComparison.Ordinal);
        Assert.Contains("| model-a | TrapImpossible | 1 | 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("| model-b | TrapWrongTest | 0 | 1 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| model-a | TrapWrongTest |", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_ends_with_the_model_not_format_note()
    {
        var markdown = new CodingBenchReportWriter().ToMarkdown(BuildReport());

        Assert.Contains("The benchmark may change a model's edit format, not the model (doc 17, conflict 3).", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_is_deterministic()
    {
        var writer = new CodingBenchReportWriter();
        var report = BuildReport();

        Assert.Equal(writer.ToMarkdown(report), writer.ToMarkdown(report));
    }

    [Fact]
    public void Json_uses_camel_case_and_string_enums()
    {
        var json = new CodingBenchReportWriter().ToJson(BuildReport());

        Assert.Contains("\"runId\": \"run-2026-11-19\"", json, StringComparison.Ordinal);
        Assert.Contains("\"format\": \"WholeFile\"", json, StringComparison.Ordinal);
        Assert.Contains("\"costSource\": \"owner-api\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"TrapImpossible\"", json, StringComparison.Ordinal);
        Assert.Contains("\n  \"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_round_trips_through_from_json()
    {
        var writer = new CodingBenchReportWriter();
        var original = BuildReport();

        var copy = CodingBenchReportWriter.FromJson(writer.ToJson(original));

        Assert.Equal(original.RunId, copy.RunId);
        Assert.Equal(original.StartedAt, copy.StartedAt);
        Assert.Equal(original.FinishedAt, copy.FinishedAt);
        Assert.Equal(original.Results.Count, copy.Results.Count);
        for (var i = 0; i < original.Results.Count; i++)
        {
            Assert.Equal(original.Results[i].Request.Model, copy.Results[i].Request.Model);
            Assert.Equal(original.Results[i].Request.Format, copy.Results[i].Request.Format);
            Assert.Equal(original.Results[i].Correct, copy.Results[i].Correct);
            Assert.Equal(original.Results[i].FakeSuccess, copy.Results[i].FakeSuccess);
            Assert.Equal(original.Results[i].Duration, copy.Results[i].Duration);
        }

        Assert.Equal(original.Summaries.Count, copy.Summaries.Count);
        for (var i = 0; i < original.Summaries.Count; i++)
        {
            Assert.Equal(original.Summaries[i], copy.Summaries[i]);
        }

        Assert.Equal(original.RecommendedFormat.Count, copy.RecommendedFormat.Count);
        foreach (var pair in original.RecommendedFormat)
        {
            Assert.Equal(pair.Value, copy.RecommendedFormat[pair.Key]);
        }
    }

    [Fact]
    public void Markdown_of_a_report_with_no_runs_still_renders()
    {
        var report = new CodingBenchReport("empty", _started, _finished, [], [], new Dictionary<string, BenchEditFormat>());

        var markdown = new CodingBenchReportWriter().ToMarkdown(report);

        Assert.Contains("No models in the report.", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void FromJson_rejects_empty_input_and_null_documents()
    {
        Assert.Throws<ArgumentException>(() => CodingBenchReportWriter.FromJson(" "));
        Assert.Throws<InvalidDataException>(() => CodingBenchReportWriter.FromJson("null"));
    }
}
