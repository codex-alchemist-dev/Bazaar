using Bazaar.Client.LocalInference.Speech.Eval;

namespace Bazaar.Client.LocalInference.Tests.Speech.Eval;

public class CleanerEvaluatorTests
{
    private static string ReadShippedCorpus() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Corpus", "cleaner-eval-corpus.json"));

    private static readonly CleanerEvalCase[] _threeCases =
    [
        new("t1", "um so the standup moves to 10:15 on, uh, Thursday", "So the standup moves to 10:15 on Thursday."),
        new("t2", "the supplier sent 240 crates and, um, 40 were damaged", "The supplier sent 240 crates and 40 were damaged."),
        new("t3", "please see https://example.com/handbook/v2 for the, uh, steps", "Please see https://example.com/handbook/v2 for the steps."),
    ];

    private static FakeLocalBackend RepliesFor(IEnumerable<CleanerEvalCase> cases, Func<CleanerEvalCase, string> reply)
    {
        var backend = new FakeLocalBackend();
        foreach (var item in cases)
        {
            backend.Reply(reply(item));
        }

        return backend;
    }

    [Fact]
    public async Task Perfect_backend_passes_every_shipped_case()
    {
        var cases = CleanerEvalCorpus.Parse(ReadShippedCorpus());
        var backend = RepliesFor(cases, c => c.Expected);

        var report = await new CleanerEvaluator(backend).RunAsync(cases, CancellationToken.None);

        Assert.Equal(50, report.Total);
        Assert.Equal(50, report.Passed);
        Assert.Equal(1.0, report.Score);
        Assert.Equal(1.0, report.ExactMatchRate);
        Assert.Equal(0, report.FellBackCount);
    }

    [Fact]
    public async Task Echoing_the_raw_transcript_fails_every_case_on_filler_removal()
    {
        var cases = CleanerEvalCorpus.Parse(ReadShippedCorpus());
        var backend = RepliesFor(cases, c => c.Input);

        var report = await new CleanerEvaluator(backend).RunAsync(cases, CancellationToken.None);

        Assert.Equal(0, report.Score);
        Assert.Equal(0, report.FillerRemovalRate);
        Assert.Equal(1.0, report.FactsKeptRate);
        Assert.Equal(1.0, report.NoAddedFactsRate);
        Assert.Equal(0, report.FellBackCount);
    }

    [Fact]
    public async Task Added_number_fails_only_the_case_that_gained_it()
    {
        var backend = RepliesFor(_threeCases, c => c.Id == "t2" ? "The supplier sent 240 crates and 40 were damaged, 99 in total." : c.Expected);

        var report = await new CleanerEvaluator(backend).RunAsync(_threeCases, CancellationToken.None);

        Assert.True(report.Cases[0].Passed);
        Assert.False(report.Cases[1].NoAddedFacts);
        Assert.False(report.Cases[1].Passed);
        Assert.True(report.Cases[2].Passed);
        Assert.Equal(2.0 / 3.0, report.Score, 6);
    }

    [Fact]
    public async Task Added_url_fails_the_no_added_facts_check()
    {
        var cases = new[] { new CleanerEvalCase("u1", "um see the page for the steps", "See the page for the steps.") };
        var backend = RepliesFor(cases, _ => "See https://example.com/new-page for the steps.");

        var result = Assert.Single((await new CleanerEvaluator(backend).RunAsync(cases, CancellationToken.None)).Cases);

        Assert.True(result.FactsKept);
        Assert.False(result.NoAddedFacts);
        Assert.False(result.Passed);
    }

    [Fact]
    public async Task Dropped_number_falls_back_to_raw_and_fails()
    {
        var backend = RepliesFor(_threeCases, c => c.Id == "t2" ? "The supplier sent crates and 40 were damaged." : c.Expected);

        var report = await new CleanerEvaluator(backend).RunAsync(_threeCases, CancellationToken.None);

        var fallback = report.Cases[1];
        Assert.True(fallback.FellBack);
        Assert.Equal(_threeCases[1].Input, fallback.Shown);
        Assert.False(fallback.FillerRemoved);
        Assert.False(fallback.Passed);
        Assert.Equal(1, report.FellBackCount);
    }

    [Fact]
    public async Task Backend_error_falls_back_to_raw_and_fails_the_case()
    {
        var backend = new FakeLocalBackend().Reply(_threeCases[0].Expected).Throw(new TimeoutException("slow")).Reply(_threeCases[2].Expected);

        var report = await new CleanerEvaluator(backend).RunAsync(_threeCases, CancellationToken.None);

        Assert.True(report.Cases[0].Passed);
        Assert.True(report.Cases[1].FellBack);
        Assert.False(report.Cases[1].Passed);
        Assert.True(report.Cases[2].Passed);
        Assert.Equal(2.0 / 3.0, report.Score, 6);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_scored()
    {
        var backend = RepliesFor(_threeCases, c => c.Expected);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new CleanerEvaluator(backend).RunAsync(_threeCases, cts.Token));
    }

    [Fact]
    public async Task Different_wording_passes_by_default_and_is_reported_as_not_an_exact_match()
    {
        var cases = new[] { new CleanerEvalCase("w1", "um Tobias Wren said the build failed on run 3", "Tobias Wren said the build failed on run 3.") };
        var backend = RepliesFor(cases, _ => "Tobias Wren reports the build failed on run 3.");

        var result = Assert.Single((await new CleanerEvaluator(backend).RunAsync(cases, CancellationToken.None)).Cases);

        Assert.True(result.Passed);
        Assert.False(result.ExactMatch);
    }

    [Fact]
    public async Task Gating_on_exact_match_fails_the_same_reply()
    {
        var cases = new[] { new CleanerEvalCase("w1", "um Tobias Wren said the build failed on run 3", "Tobias Wren said the build failed on run 3.") };
        var backend = RepliesFor(cases, _ => "Tobias Wren reports the build failed on run 3.");
        var options = new CleanerEvalOptions { GateOnExactMatch = true };

        var result = Assert.Single((await new CleanerEvaluator(backend, options).RunAsync(cases, CancellationToken.None)).Cases);

        Assert.False(result.Passed);
        Assert.False(result.ExactMatch);
    }

    [Fact]
    public async Task Configured_filler_list_replaces_the_default_list()
    {
        var cases = new[] { new CleanerEvalCase("f1", "um so there are 5 items", "So there are 5 items.") };
        var backend = RepliesFor(cases, _ => "um so there are 5 items");
        var options = new CleanerEvalOptions { Fillers = ["well"] };

        var result = Assert.Single((await new CleanerEvaluator(backend, options).RunAsync(cases, CancellationToken.None)).Cases);

        Assert.True(result.FillerRemoved);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Filler_match_is_whole_word_and_ignores_case()
    {
        var cases = new[]
        {
            new CleanerEvalCase("m1", "um the umbrella is 2 metres", "The umbrella is 2 metres."),
            new CleanerEvalCase("m2", "I mean the umbrella is 2 metres", "The umbrella is 2 metres."),
        };
        var backend = RepliesFor(cases, c => c.Id == "m1" ? "The umbrella is 2 metres." : "The umbrella is 2 metres. Um, sure.");

        var report = await new CleanerEvaluator(backend).RunAsync(cases, CancellationToken.None);

        Assert.True(report.Cases[0].Passed);
        Assert.False(report.Cases[1].FillerRemoved);
        Assert.False(report.Cases[1].Passed);
    }

    [Fact]
    public async Task Empty_case_list_scores_zero_without_calling_the_backend()
    {
        var backend = new FakeLocalBackend();

        var report = await new CleanerEvaluator(backend).RunAsync([], CancellationToken.None);

        Assert.Equal(0, report.Total);
        Assert.Equal(0, report.Score);
        Assert.Equal(0, backend.CallCount);
    }

    [Fact]
    public void Blank_filler_is_rejected()
    {
        var options = new CleanerEvalOptions { Fillers = [" "] };

        Assert.Throws<ArgumentException>(() => new CleanerEvaluator(new FakeLocalBackend(), options));
    }
}
