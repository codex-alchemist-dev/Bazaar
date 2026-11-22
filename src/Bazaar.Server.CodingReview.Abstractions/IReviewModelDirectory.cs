namespace Bazaar.Server.CodingReview;

/// <summary>
/// Port: the reviewer pool for an author. The implementation returns only the models the author's whitelist (per-agent model
/// policy) shows, so the chooser never picks a model the author could not use.
/// </summary>
public interface IReviewModelDirectory
{
    /// <summary>The pool for an author model, or null when the author's model is unknown.</summary>
    Task<ReviewerPool?> GetPoolAsync(string authorModelId, CancellationToken cancellationToken);
}
