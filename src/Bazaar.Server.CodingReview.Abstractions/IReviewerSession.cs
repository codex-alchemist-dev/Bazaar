namespace Bazaar.Server.CodingReview;

/// <summary>What the reviewer session is given: the context, the model it runs on, and the only tool names it may call.</summary>
public sealed record ReviewerSessionRequest(ReviewContext Context, string ReviewerModelId, IReadOnlyList<string> AllowedTools);

/// <summary>What the reviewer session returns. <see cref="Body"/> is agent output, so it is soft-tainted, not instructions.</summary>
public sealed record ReviewerSessionResult(bool Approved, string Body);

/// <summary>
/// Port: the reviewer model run, with read-only tools only. The runtime must enforce <see cref="ReviewerSessionRequest.AllowedTools"/>.
/// The server enforces it in tool dispatch, never in the prompt.
/// </summary>
public interface IReviewerSession
{
    /// <summary>Runs one review and returns its verdict.</summary>
    Task<ReviewerSessionResult> RunAsync(ReviewerSessionRequest request, CancellationToken cancellationToken);
}
