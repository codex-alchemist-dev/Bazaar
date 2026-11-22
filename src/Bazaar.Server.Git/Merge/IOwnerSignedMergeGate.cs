namespace Bazaar.Server.Git.Merge;

/// <summary>
/// The only way to a squash merge (doc 21, doc 52). It is the sole caller of <see cref="IForgeMergeExecutor"/>. Agent messages
/// never reach it; the owner's signed approval does.
/// </summary>
public interface IOwnerSignedMergeGate
{
    /// <summary>Merges only if every check holds, otherwise throws <see cref="MergeNotApprovedException"/> before any executor call.</summary>
    Task MergeAsync(OwnerMergeApproval approval, MergeRequest merge, CancellationToken cancellationToken);
}
