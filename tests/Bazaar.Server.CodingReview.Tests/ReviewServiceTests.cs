using Bazaar.Server.CodingReview.InMemory;

namespace Bazaar.Server.CodingReview.Tests;

public sealed class ReviewServiceTests
{
    private static InMemoryReviewModelDirectory Directory(params ReviewerPool[] pools)
    {
        var directory = new InMemoryReviewModelDirectory();
        foreach (var pool in pools)
        {
            directory.Add(pool);
        }

        return directory;
    }

    private static ReviewerPool OtherFamilyPool() =>
        ReviewFixtures.Pool("author-1", "alpha", new ReviewModelOption("zeta-1", "zeta"));

    [Fact]
    public async Task An_approval_sets_the_flag_and_posts_the_review_and_nothing_else()
    {
        var sources = new InMemoryReviewContextSources { Diff = [ReviewFixtures.File("src/a.cs", "x")] };
        var sink = new InMemoryReviewResultSink();
        var reviewer = new ScriptedReviewerSession(new ReviewerSessionResult(true, "Looks correct."));
        var service = ReviewFixtures.Service(sources, Directory(OtherFamilyPool()), reviewer, sink);

        var outcome = await service.ReviewAsync(ReviewFixtures.PullRequest, "author-1", TestContext.Current.CancellationToken);

        Assert.Equal(ReviewVerdict.Approved, outcome.Verdict);
        Assert.Equal([(ReviewFixtures.PullRequest, true)], sink.Flags.ToArray());
        var posted = Assert.Single(sink.Posted);
        Assert.Equal(ReviewVerdict.Approved, posted.Verdict);
        Assert.Equal("Looks correct.", posted.Body);
    }

    [Fact]
    public async Task A_rejection_clears_the_flag_and_is_not_approved()
    {
        var sink = new InMemoryReviewResultSink();
        var reviewer = new ScriptedReviewerSession(new ReviewerSessionResult(false, "Missing test."));
        var service = ReviewFixtures.Service(new InMemoryReviewContextSources(), Directory(OtherFamilyPool()), reviewer, sink);

        var outcome = await service.ReviewAsync(ReviewFixtures.PullRequest, "author-1", TestContext.Current.CancellationToken);

        Assert.Equal(ReviewVerdict.NotApproved, outcome.Verdict);
        Assert.Equal([(ReviewFixtures.PullRequest, false)], sink.Flags.ToArray());
    }

    [Fact]
    public async Task The_reviewer_gets_the_read_only_allowlist_and_the_other_family_model()
    {
        var reviewer = new ScriptedReviewerSession(new ReviewerSessionResult(true, "ok"));
        var service = ReviewFixtures.Service(new InMemoryReviewContextSources(), Directory(OtherFamilyPool()), reviewer, new InMemoryReviewResultSink());

        await service.ReviewAsync(ReviewFixtures.PullRequest, "author-1", TestContext.Current.CancellationToken);

        Assert.NotNull(reviewer.LastRequest);
        Assert.Equal("zeta-1", reviewer.LastRequest.ReviewerModelId);
        Assert.Equal(ReviewerToolSet.AllowedToolNames, reviewer.LastRequest.AllowedTools);
    }

    [Fact]
    public async Task A_same_family_review_is_recorded_as_same_family_in_the_outcome_and_the_report()
    {
        var sink = new InMemoryReviewResultSink();
        var pool = ReviewFixtures.Pool("author-1", "alpha", new ReviewModelOption("alpha-2", "alpha"));
        var service = ReviewFixtures.Service(
            new InMemoryReviewContextSources(),
            Directory(pool),
            new ScriptedReviewerSession(new ReviewerSessionResult(true, "ok")),
            sink);

        var outcome = await service.ReviewAsync(ReviewFixtures.PullRequest, "author-1", TestContext.Current.CancellationToken);

        Assert.True(outcome.SameFamily);
        Assert.True(sink.Posted[0].SameFamily);
        Assert.Equal("alpha-2", outcome.ReviewerModelId);
    }

    [Fact]
    public async Task With_no_reviewer_pool_the_review_does_not_run_and_the_change_is_not_approved()
    {
        var sink = new InMemoryReviewResultSink();
        var reviewer = new ScriptedReviewerSession(new ReviewerSessionResult(true, "should not run"));
        var service = ReviewFixtures.Service(new InMemoryReviewContextSources(), Directory(), reviewer, sink);

        var outcome = await service.ReviewAsync(ReviewFixtures.PullRequest, "unknown-author", TestContext.Current.CancellationToken);

        Assert.Equal(ReviewVerdict.NotCompleted, outcome.Verdict);
        Assert.Null(outcome.ReviewerModelId);
        Assert.Null(reviewer.LastRequest);
        Assert.Equal([(ReviewFixtures.PullRequest, false)], sink.Flags.ToArray());
    }

    [Fact]
    public async Task A_failed_reviewer_session_is_not_approved_and_clears_the_flag()
    {
        var sink = new InMemoryReviewResultSink();
        var reviewer = new ScriptedReviewerSession(null, new InvalidOperationException("model timed out"));
        var service = ReviewFixtures.Service(new InMemoryReviewContextSources(), Directory(OtherFamilyPool()), reviewer, sink);

        var outcome = await service.ReviewAsync(ReviewFixtures.PullRequest, "author-1", TestContext.Current.CancellationToken);

        Assert.Equal(ReviewVerdict.NotCompleted, outcome.Verdict);
        Assert.Equal([(ReviewFixtures.PullRequest, false)], sink.Flags.ToArray());
        Assert.DoesNotContain("model timed out", sink.Posted[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_outcome_carries_the_truncation_flag_for_doc_11()
    {
        var sources = new InMemoryReviewContextSources
        {
            Diff = [ReviewFixtures.File("src/a.cs", "0123456789")],
        };
        var service = new ReviewService(
            sources,
            Directory(OtherFamilyPool()),
            new ScriptedReviewerSession(new ReviewerSessionResult(true, "ok")),
            new InMemoryReviewResultSink(),
            new ReviewContextBuilder(new ReviewContextLimits(MaxDiffCharacters: 3)));

        var outcome = await service.ReviewAsync(ReviewFixtures.PullRequest, "author-1", TestContext.Current.CancellationToken);

        Assert.True(outcome.ContextTruncated);
        Assert.Equal("author-1", outcome.AuthorModelId);
    }
}
