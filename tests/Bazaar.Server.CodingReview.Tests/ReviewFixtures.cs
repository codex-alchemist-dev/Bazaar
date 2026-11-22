using Bazaar.Server.CodingReview.InMemory;

namespace Bazaar.Server.CodingReview.Tests;

/// <summary>Shared builders for the review tests. Keeps each test to the one fact it checks.</summary>
internal static class ReviewFixtures
{
    public static ReviewPullRequestRef PullRequest { get; } = new("example/repo", 42, "base1", "head1");

    public static UntrustedText Diff(string text) => UntrustedText.From(UntrustedTextSource.Diff, text);

    public static UntrustedText PathText(string path) => UntrustedText.From(UntrustedTextSource.Path, path);

    public static DiffFile File(string path, params string[] hunkBodies) =>
        new(PathText(path), ReviewChangeKind.Modified, hunkBodies.Select((b, i) => new DiffHunk(i + 1, i + 1, Diff(b))).ToList());

    public static ReviewerPool Pool(string authorModel, string authorFamily, params ReviewModelOption[] candidates) =>
        new(authorModel, authorFamily, candidates);

    public static ReviewCheckSummary Checks(bool passed, params ReviewCheckFlag[] flags) =>
        new(passed, passed ? 0 : 1, flags);

    public static ReviewCheckFlag Flag(string code, string? path = null, string detail = "detail") =>
        new(code, path, UntrustedText.From(UntrustedTextSource.CheckOutput, detail));

    public static ReviewService Service(
        InMemoryReviewContextSources sources,
        InMemoryReviewModelDirectory models,
        IReviewerSession reviewer,
        InMemoryReviewResultSink sink,
        ReviewContextLimits? limits = null) =>
        new(sources, models, reviewer, sink, new ReviewContextBuilder(limits ?? ReviewContextLimits.Default));
}

/// <summary>A reviewer session that returns a fixed result or throws, and records the request it was given.</summary>
internal sealed class ScriptedReviewerSession(ReviewerSessionResult? result, Exception? failure = null) : IReviewerSession
{
    public ReviewerSessionRequest? LastRequest { get; private set; }

    public Task<ReviewerSessionResult> RunAsync(ReviewerSessionRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (failure is not null)
        {
            throw failure;
        }

        return Task.FromResult(result ?? throw new InvalidOperationException("No scripted result."));
    }
}
