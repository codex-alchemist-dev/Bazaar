namespace Bazaar.Server.Souls.Judgments;

/// <summary>A judgment message to post to a GC.</summary>
/// <param name="GcId">The GC to post to.</param>
/// <param name="AgentId">The agent principal that writes the message.</param>
/// <param name="DecisionId">The decision reference. Makes the post idempotent: a repeat posts nothing new.</param>
/// <param name="Text">The message body.</param>
public sealed record JudgmentPost(Guid GcId, Guid AgentId, Guid DecisionId, string Text);

/// <summary>Where a judgment message landed.</summary>
/// <param name="Seq">The GC sequence number of the message's event.</param>
/// <param name="Duplicate">True when this decision had already been posted and the original event was returned.</param>
public sealed record JudgmentPostReceipt(long Seq, bool Duplicate);

/// <summary>Port that posts a judgment message to a GC (docs/design/01, the message write path).</summary>
public interface IGcJudgmentPoster
{
    /// <summary>Posts the message, or returns the original event when this decision was already posted.</summary>
    Task<JudgmentPostReceipt> PostAsync(JudgmentPost post, CancellationToken cancellationToken);
}
