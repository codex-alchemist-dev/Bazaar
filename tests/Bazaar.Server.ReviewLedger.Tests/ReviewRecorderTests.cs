using Bazaar.Server.ReviewLedger;
using Bazaar.Server.ReviewLedger.Abstractions;

namespace Bazaar.Server.ReviewLedger.Tests;

public class ReviewRecorderTests
{
    private static (ReviewRecorder Recorder, InMemoryReviewStore Store) Build()
    {
        var store = new InMemoryReviewStore();
        return (new ReviewRecorder(store, new ReviewCaptureSettings(), TimeProvider.System), store);
    }

    private static ReviewRecord Review(string reviewer = "r1", string pr = "org/r#1", int overall = 4, Guid? supersedes = null) =>
        new(pr, reviewer, "author", "csharp", overall, true, SupersedesId: supersedes);

    [Fact]
    public async Task A_valid_review_is_recorded()
    {
        var (recorder, store) = Build();
        var result = await recorder.RecordAsync(Review(), TestContext.Current.CancellationToken);
        Assert.True(result.Recorded);
        Assert.Single(store.Rows);
    }

    [Fact]
    public async Task Self_review_is_refused()
    {
        var (recorder, store) = Build();
        var result = await recorder.RecordAsync(Review(reviewer: "author"), TestContext.Current.CancellationToken);
        Assert.Equal(ReviewRefusal.SelfReview, result.Refusal);
        Assert.Empty(store.Rows);
    }

    [Fact]
    public async Task A_third_reviewer_is_refused_but_a_second_review_by_a_listed_reviewer_is_allowed()
    {
        var (recorder, _) = Build();
        await recorder.RecordAsync(Review("r1"), TestContext.Current.CancellationToken);
        await recorder.RecordAsync(Review("r2"), TestContext.Current.CancellationToken);

        var third = await recorder.RecordAsync(Review("r3"), TestContext.Current.CancellationToken);
        var again = await recorder.RecordAsync(Review("r1", overall: 2), TestContext.Current.CancellationToken);

        Assert.Equal(ReviewRefusal.TooManyReviewers, third.Refusal);
        Assert.True(again.Recorded);
    }

    [Fact]
    public async Task Another_pr_has_its_own_cap()
    {
        var (recorder, _) = Build();
        await recorder.RecordAsync(Review("r1"), TestContext.Current.CancellationToken);
        await recorder.RecordAsync(Review("r2"), TestContext.Current.CancellationToken);
        Assert.True((await recorder.RecordAsync(Review("r3", pr: "org/r#2"), TestContext.Current.CancellationToken)).Recorded);
    }

    [Fact]
    public async Task Superseding_keeps_the_old_row_and_must_match_reviewer_and_pr()
    {
        var (recorder, store) = Build();
        var first = await recorder.RecordAsync(Review(), TestContext.Current.CancellationToken);

        var ok = await recorder.RecordAsync(Review(overall: 2, supersedes: first.ReviewId), TestContext.Current.CancellationToken);
        var other = await recorder.RecordAsync(Review("r2", supersedes: first.ReviewId), TestContext.Current.CancellationToken);
        var unknown = await recorder.RecordAsync(Review(supersedes: Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.True(ok.Recorded);
        Assert.Equal(2, store.Rows.Count);
        Assert.Equal(ReviewRefusal.SupersedesMismatch, other.Refusal);
        Assert.Equal(ReviewRefusal.SupersedesUnknown, unknown.Refusal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Overall_outside_one_to_five_throws(int overall)
    {
        var (recorder, _) = Build();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => recorder.RecordAsync(Review(overall: overall), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_rubric_must_be_a_json_object()
    {
        var (recorder, _) = Build();
        await Assert.ThrowsAsync<ArgumentException>(() => recorder.RecordAsync(Review() with { RubricJson = "[1]" }, TestContext.Current.CancellationToken));
        Assert.True((await recorder.RecordAsync(Review() with { RubricJson = "{\"correctness\":4}" }, TestContext.Current.CancellationToken)).Recorded);
    }
}
